# flex-iq-noise

A bench rig that transmits **band-limited complex Gaussian white noise** through a FlexRadio 6000 via
the Waveform API, to characterise the radio's IQ transmit bandwidth.

The point of noise rather than tones: it excites every frequency in the band at once, so a single
burst plus one receiver capture shows the *whole* transmit response — where it is flat, where it
rolls off, and how fast — instead of one point per tone.

```sh
dotnet run --project tools/FlexIqNoise -- --radio 10.45.0.76 --freq 14.200 --bw 8k --seconds 10
```

That puts flat noise across **14.192 to 14.200 MHz** and nothing outside it.

**The band is placed below the carrier by default**, because only content below DC is transmitted —
in every mode. So `--bw 10k` occupies `--freq` minus 10 kHz up to `--freq`, and the whole requested
width reaches the air. There is deliberately no option to select the other side: it would be a named
choice between one correct value and two that quietly lose signal. `--offset` still lets you place
energy anywhere on purpose — including above DC to confirm nothing comes out — and warns before
keying when part or all of the request cannot be transmitted. The measured ceiling is **10 kHz
one-sided**, confirmed on air at 14.190–14.200.

## The signal

Complex I/Q Gaussian noise synthesised at the waveform's 24 kHz complex rate — so the widest band
this tool can *synthesise* is ±12 kHz, of which the radio will actually *transmit* about 10 kHz on one
side of the carrier (see the measurements below). Three stages:

1. **Box–Muller** gives two independent N(0,σ) samples per complex sample — I and Q.
2. A **linear-phase windowed-sinc FIR** (4-term Blackman–Harris, ~−92 dB stopband) low-passes both
   components at `bw/2`. A real symmetric kernel on a complex signal keeps the passband symmetric
   about DC, so the result occupies exactly ±`bw`/2 about the carrier. Tap count is picked so the
   rig's own transition band is ~2 % of the requested bandwidth — far sharper than any radio, so a
   measured skirt belongs to the radio and not to this filter.
3. An optional NCO shifts the band off centre (`--offset`).

σ is pre-compensated for the filter's noise gain, so the transmitted RMS is the requested one
whatever the bandwidth — a narrow band and a wide one hit the PA equally hard.

## Trust the instrument first

`--dry-run` generates and analyses the burst without keying, so you can confirm the rig before
attributing anything to the radio:

```
$ flex-iq-noise --freq 14.200 --bw 2k --seconds 8 --dry-run

signal
  shaping    4801-tap Blackman–Harris FIR, 40 Hz transition
  rms        0.1491 per component (asked 0.1500)
  peak       0.6929  →  crest 13.3 dB
  clipped    0

spectrum   (Welch, 4096-point, 5.9 Hz bins)
  in band    flat to ±0.46 dB (1σ across the middle 80 %)
  −3  dB        1992 Hz wide   (-996 … +996 Hz from centre)
  −20 dB        2016 Hz wide   (-1008 … +1008 Hz from centre)
  −60 dB        2062 Hz wide   (-1031 … +1031 Hz from centre)
  99 % OBW      1969 Hz wide   (-984 … +984 Hz from centre)
  floor         -155 dB beyond ±2000 Hz
```

±0.46 dB is the Welch estimator's own variance at this segment count, i.e. genuinely flat. The
skirts reach −60 dB within 31 Hz of the nominal edge and the floor is at the float32 noise level.

`--csv` writes the measured spectrum for plotting; `--wav` writes the exact IQ handed to the radio as
a 2-channel float32 WAV (ch1 = I, ch2 = Q) for Audacity / inspectrum / GNU Radio.

The same measurements are printed after every real transmit, so a burst that went out wrong says so.

## Guards

The rig refuses to let you mistake its own artefacts for the radio's behaviour:

- **Clipping.** Gaussian peaks are unbounded; a clipped sample splatters broadband. Any clipping is
  counted and flagged, and the run exits non-zero. (`--rms 0.45` on a 2 kHz band blows the −60 dB
  width from 2.1 kHz to 18.6 kHz — that is the guard earning its keep.)
- **Starvation.** The waveform is reflection-driven: the radio pulls 128 complex samples 187.5 times
  a second and any shortfall is zero-filled, which also splatters. A second of noise is queued
  *before* keying, and the starve count must be zero.
- **A radio that never pulls.** If transmit buffers stop arriving for 10 s the run aborts and unkeys
  rather than blocking forever on back-pressure.
- **Aliasing.** A band that would fall outside ±12 kHz is rejected at parse time rather than folding
  back in-band.

## Explore mode — walk the passband by hand

```sh
flex-iq-noise --radio <ip> --freq 14.200 --explore -500
```

Keys once and transmits a single tone you retune live while watching a receiver:

| Key | Step |
|---|---|
| up / down | ±100 Hz |
| left / right | ±10 Hz |
| PgUp / PgDn | ±1000 Hz |
| `0` | back to the carrier |
| `q` or Esc | unkey and quit |

**The tone moves because the IQ is regenerated at a new offset — the dial never moves.** That is the
whole point: retuning the slice would drag the passband along with the tone and measure nothing,
whereas holding the slice still and sweeping the signal through it walks the real edges. Phase is
carried across each step rather than reset, so a retune does not click and splatter across the band
being measured.

The status line predicts what the radio should do with each offset — is it in the half that
transmits, is it inside the transmit filter, does this mode mirror? — so you can watch prediction and
reality diverge at the edges:

```
  tone   -800 Hz   14.199200 MHz  expect: PASS
  tone   +600 Hz   14.200600 MHz  expect: blocked, +ve baseband
  tone -10800 Hz   14.189200 MHz  expect: blocked, past 10000 Hz filter
```

**Start negative, in every mode.** Only the negative half of the baseband is transmitted, so
`--explore 500` is silent until you walk it down through zero. The default is `-500` for that reason.
Under a mirroring mode (`IQ`/`USB`/`DIGU`) the tone still appears, but *above* the carrier and moving
the opposite way — the tool predicts that too, and watching the direction of travel reverse is the
clearest demonstration of the inversion there is.

The transmit filter is opened to the radio's 10 kHz maximum for this mode, so what you find is the
radio's edge and not a filter this tool imposed. Latency from keypress to air is ~50 ms: the
transmit queue is held deliberately shallow rather than letting the ring buffer fill, or every press
would take a buffer-depth to be heard.

## Diagnosing a wrong-looking band

Noise centred on the carrier is **symmetric**, so it cannot tell you *why* a band looks wrong — a
dropped sideband and an inverted spectrum look identical. A single tone can. Run it twice:

```sh
flex-iq-noise --radio <ip> --freq 14.200 --tone 3k  --seconds 10
flex-iq-noise --radio <ip> --freq 14.200 --tone -3k --seconds 10
```

| Where a **single** tone lands | Verdict |
|---|---|
| −3k → 14.197 (below the carrier) | mode is **upright** — usable for a modulated signal |
| −3k → 14.203 (above the carrier) | mode **mirrors** — a modulated signal goes out inverted |
| +3k → anywhere | should never happen; positive baseband is transmitted by no mode |
| nothing at all | the transmit filter is cutting it, or the mode discards Q |

Confirm with the opposite sign: `--tone 3k` should be **silent in every mode**. If it isn't, the model
above is wrong and worth saying so.

## Explore mode — walk the passband by hand

```sh
flex-iq-noise --radio <ip> --freq 14.200 --explore -500
```

Keys once and transmits a single tone you retune live while watching a receiver:

| Key | Step |
|---|---|
| up / down | ±100 Hz |
| left / right | ±10 Hz |
| PgUp / PgDn | ±1000 Hz |
| `0` | back to the carrier |
| `q` or Esc | unkey and quit |

**The tone moves because the IQ is regenerated at a new offset — the dial never moves.** That is the
whole point: retuning the slice would drag the passband along with the tone and measure nothing,
whereas holding the slice still and sweeping the signal through it walks the real edges. Phase is
carried across each step rather than reset, so a retune does not click and splatter across the band
being measured.

The status line predicts what the radio should do with each offset — is it in the half that
transmits, is it inside the transmit filter, does this mode mirror? — so you can watch prediction and
reality diverge at the edges:

```
  tone   -800 Hz   14.199200 MHz  expect: PASS
  tone   +600 Hz   14.200600 MHz  expect: blocked, +ve baseband
  tone -10800 Hz   14.189200 MHz  expect: blocked, past 10000 Hz filter
```

**Start negative, in every mode.** Only the negative half of the baseband is transmitted, so
`--explore 500` is silent until you walk it down through zero. The default is `-500` for that reason.
Under a mirroring mode (`IQ`/`USB`/`DIGU`) the tone still appears, but *above* the carrier and moving
the opposite way — the tool predicts that too, and watching the direction of travel reverse is the
clearest demonstration of the inversion there is.

The transmit filter is opened to the radio's 10 kHz maximum for this mode, so what you find is the
radio's edge and not a filter this tool imposed. Latency from keypress to air is ~50 ms: the
transmit queue is held deliberately shallow rather than letting the ring buffer fill, or every press
would take a buffer-depth to be heard.

## Diagnosing a wrong-looking band

Noise centred on the carrier is **symmetric**, so it cannot tell you *why* a band looks wrong — a
dropped sideband and an inverted spectrum look identical. A single tone can. Run it twice:

```sh
flex-iq-noise --radio <ip> --freq 14.200 --tone 3k  --seconds 10
flex-iq-noise --radio <ip> --freq 14.200 --tone -3k --seconds 10
```

| Where they land | Verdict |
|---|---|
| +3k → 14.203 and −3k → 14.197 | complex IQ, correct orientation — the path is good |
| +3k → 14.197 and −3k → 14.203 | complex but **inverted** (I/Q swapped somewhere) |
| one lands correctly, the other is **silent** | complex and correctly oriented, but the path is **single-sideband** |
| both on the **same** frequency | the path is **real** — Q ignored, radio SSB-modulating I alone |
| neither appears | the transmit filter is cutting them |

The single-sideband case is the one that **halves a requested band and leaves it on one side of the
carrier**: ask for 3 kHz, get 1.5 kHz. Note that the IQ path itself is fine here — a real-only path
could not distinguish +3k from −3k at all, because both are the same I waveform, so a tone appearing
for one sign and not the other is positive proof the complex path works. What is wrong is sideband
selection. To find a mode that passes both halves:

```sh
flex-iq-noise --radio <ip> --freq 14.200 --sweep --seconds 8
```

That transmits a **single** tone at −3 kHz under every `underlying_mode` in turn — RAW, IQ, USB, LSB,
DIGU, DIGL, AM, FM — with a gap between segments and a printed schedule. Which side of the carrier it
lands on identifies each mode outright. A symmetric probe cannot do this: it contains both tones, so
"the +f tone passed" and "the −f tone passed and the mode inverts" look identical, which is exactly
how the mode table came out wrong the first time.

The rig's own tone is clean to **193 dB** of image rejection — independently confirmed against numpy
— so any mirror you see on air is the radio's.

## No radio? Use the mock

`--radio mock` runs the entire path — waveform registration, keying, the VITA transport, the
big-endian float32 packetize — against the in-process `MockFlexRadio`, and additionally reports the
spectrum of what the "radio" received. No RF, no PA, full coverage:

```sh
dotnet run --project tools/FlexIqNoise -- --radio mock --freq 14.200 --bw 2k --seconds 6
```

## Measured: the 6500's waveform modes are single-sideband

Measured on M0LTE's FLEX-6500 (firmware 4.1.5, API V1.4.0.0), 2026-07-26, against an external
receiver. **Only the negative half of the baseband ever reaches the air**, in every mode — positive
frequency content is simply not transmitted. What the mode chooses is which side of the carrier the
surviving half lands on, and therefore whether your spectrum arrives upright or mirrored:

| `underlying_mode` | A baseband tone at −3 kHz appears at | Spectrum |
|---|---|---|
| **RAW**, LSB, DIGL | carrier **−** 3 kHz (14.197) | **upright** |
| IQ, USB, DIGU | carrier **+** 3 kHz (14.203) | **mirrored** |
| AM, FM | both sidebands + carrier at 14.200 (FM adds spurs) | Q discarded entirely |

Verified by `--sweep`, which sends one asymmetric tone per mode so which side it lands on *is* the
answer. Confirmed interactively too: under RAW, walking the tone more negative moves it **down** in
frequency; under DIGU the same keypresses move it **up** — the inversion made visible, which no
static tone or noise band can show.

A baseband tone at **+3 kHz produces nothing at all** under any of them.

### How a symmetric probe hid this — twice

The first sweep used a two-tone probe at **±3 kHz**, and under DIGU it put one tone at 14.203. That
reads naturally as "the +3 kHz tone passed" — but it is equally consistent with "the −3 kHz tone
passed and the mode inverts". **A symmetric probe cannot tell those apart**, because it contains both
tones. The same blind spot had already produced §9.5's wrong wideband conclusion from a symmetric
comb, and it recurred here for the upper-sideband family after being correctly resolved for RAW.

Only an **asymmetric** probe decides it: transmit a single tone at −3 kHz and see which side of the
carrier it lands on, then a single tone at +3 kHz and confirm silence. That is what `--tone` is for,
and why `--sweep` now sends one tone rather than a symmetric pair.

### The real cap: the radio's transmit filter, default 3 kHz, max 10 kHz

The `transmit` status object carries the filter that actually governs occupied bandwidth:

```
transmit   lo=0 hi=3000 tx_filter_changes_allowed=1 tx_slice_mode=NOIS
```

Three things had to be untangled to find it:

- It is **not** the waveform's `tx_filter`. `waveform set <name> tx_filter low_cut=/high_cut=`
  returns `err=0` and has no effect at all on this firmware.
- It is **not** the slice's `filter_lo`/`filter_hi` (300/3000 above) — that is the *receive* filter,
  and `filt <idx> <lo> <hi>` likewise returns `err=0` without changing the transmit passband.
- It is read back as **`lo`/`hi`** but set with **`filter_low=`/`filter_high=`**, which is why a
  probe that looked for `filter_high` in the status found nothing and read as "unchanged".

The default `hi=3000` is exactly the ~3 kHz SSB passband measured on air. Raising it works, and
**clamps hard at 10 000 Hz**:

| `transmit set filter_high=` | resulting `hi` |
|---|---|
| 3000 | 3000 |
| 4000 | 4000 |
| 6000 | 6000 |
| 10000 | 10000 |
| 11000 | **10000** |
| 20000 | **10000** |

**So the usable Waveform-API IQ transmit bandwidth on a 6500 is ~10 kHz, one-sided** — not the 3 kHz
the defaults give you, and not the ±12 kHz §9.5 implies.

The rig now sets this filter automatically to cover whatever `--bw`/`--offset`/`--tone` asks for, and
warns when the request exceeds the 10 kHz clamp. `--transmit-filter <lo>,<hi>` overrides it.

**Note it is a global radio setting.** It persists after the run and affects ordinary SSB transmit —
the factory value is `lo=0 hi=3000`, restorable with
`--post "transmit set filter_low=0" --post "transmit set filter_high=3000"`.

### This overturned `docs/flex-integration.md` §9.5

§9.5 in `pdn-soundmodem` concluded `underlying_mode=RAW` carries "true wideband complex IQ→RF, both
sidebands", giving ~14–20 kHz usable. That was measured with a **symmetric** comb (±3 and ±7 kHz),
which contains both the `+f` and `−f` tone — so a single-sideband path reproduces it looking
symmetric, and the probe could not decide the question it was used for. **§9.5 has been corrected**
(commit `docs(flex): correct §9.5 …`) to record the measured behaviour and to budget ~10 kHz
one-sided for the wideband own-mode roadmap items.

(The same trap bites this tool's own report: with a symmetric `--tone 3k,-3k` probe each tone is the
other's mirror, so the image-rejection column is 0 dB by construction. It is suppressed there rather
than read as a verdict.)

## Measuring the other end

This rig is the transmit half only. For the receive half see `docs/flex-integration.md` §9.5 in
`pdn-soundmodem`: DAX-IQ self-capture **does not work** (the receiver is blanked during transmit —
the comb is simply absent), so an external receiver is required. The UberSDR `iq96` route is
documented there, including the `User-Agent` requirement and the zstd/PCM frame format.

§9.5 also carries the corrected transmit-side findings above.

## Options

Run with `--help`. Frequencies accept `14.2`, `14200k`, `14.2M`, `14200000Hz`; a bare `--freq` number
is MHz, a bare `--bw` number is Hz.

Exit code is 0 only when the burst went out cleanly and the rig's own spectrum is trustworthy.
