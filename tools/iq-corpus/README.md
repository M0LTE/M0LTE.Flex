# IQ test corpus

Complex baseband at **24 kHz** — the FlexRadio waveform rate — as interleaved samples with
no header. Transmit one with:

```sh
flex-iq-tx --radio <ip> --freq 14.200 --bw <width> --direct < tone-minus3k.cf32
```

Every file is already positioned **below DC** — the only half a Flex waveform transmits —
so they are transmitted with **`--direct`**, which sends the samples verbatim at the frequency
you name. Expected results below are all relative to that tuned frequency. The one
exception is `tone-plus3k`, deliberately placed in the half that should stay silent.

(Without `--direct`, `flex-iq-tx` instead *places* a DC-centred or `0..bw` stream for you —
that is the path for a modulator's output, not for these pre-positioned files.)

## Format

`cf32` is interleaved little-endian **float32**, ±1.0 full scale, **no header** — byte-identical
to GNU Radio's `complex64` and to a `.cfile`. It reads directly as `numpy.complex64`, and opens
in GNU Radio, inspectrum, SDR#, and `sox -t raw -e float -b 32`. `cs16` is interleaved
little-endian int16 at ±32767. Both are written explicitly little-endian, so they are
host-independent.

A bare `.cf32` carries no sample rate — get it wrong and every frequency you read off the file
is wrong by the same factor, silently. Add `--sigmf` to write `.sigmf-data` + `.sigmf-meta`
pairs instead, which carry the rate and datatype with them.

`flex-iq-tx --in <file>` picks up a `.sigmf-meta` sidecar automatically: it takes the
datatype from it and **checks** the sample rate against the waveform's 24 kHz, refusing a
mismatch rather than transmitting a signal at the wrong width with every frequency scaled.
Piping a bare `.cf32` on stdin still works; you just own the rate yourself.

Regenerate with `flex-iq-gen --corpus <dir>` (add `--sigmf` for the self-describing form).
Files are byte-identical for a given seed.

| File | Proves | Expected on air |
|---|---|---|
| `tone-minus3k.cf32` | Sideband and orientation, decisively. | A single carrier 3 kHz BELOW the tuned frequency. Above it instead means the mode mirrors; nothing at all means the mode does not transmit our IQ. |
| `tone-plus3k.cf32` | The falsification test — the half that should never transmit. | SILENCE (carrier leakage only). Anything else overturns the model that only content below DC reaches the air, and should be reported. |
| `twotone-2k-5k.cf32` | Spacing and order across an upright path. | Two carriers 2 kHz and 5 kHz below the tuned frequency. Under a mirrored path they appear above it, with the 5 kHz one furthest out — the order relative to the carrier reverses. |
| `noise-3k.cf32` | An ordinary narrow channel, end to end. | Flat noise filling the 3 kHz immediately below the tuned frequency, sharp edges, nothing above it. |
| `noise-10k.cf32` | The measured ceiling. | Flat noise filling the 10 kHz below the tuned frequency. This is the widest the radio's transmit filter will pass; ask for more and it truncates. |
| `chirp-10k.cf32` | Where the passband actually ends. | A sweep climbing from 10 kHz below the tuned frequency up to it. On a waterfall the line starts wherever the transmit filter opens — reading the edge off directly. |
| `staircase-10k.cf32` | Orientation, without needing a demodulator. | Five noise steps 6 dB apart across the 10 kHz below the tuned frequency, STRONGEST at the bottom. If the steps ascend instead, the path is mirrored — which flat noise and single tones cannot show. |
| `qpsk-2k4.cf32` | A real modulated signal. | A ~3.2 kHz QPSK signal centred 2 kHz below the tuned frequency. Its failure modes — mirroring, companding, clipping — leave the spectrum looking plausible, so this is the entry that needs a receiver rather than an eye. |

## Suggested order

1. `tone-minus3k` — confirms the path transmits at all, and which way up.
2. `tone-plus3k` — confirms the other half really is silent. If it is not, stop: the model
   is wrong and everything below is suspect.
3. `staircase-10k` — confirms orientation across a whole band, not just at one frequency.
4. `chirp-10k` — reads the passband edges off a waterfall.
5. `noise-3k`, `noise-10k` — ordinary and maximum-width channels.
6. `qpsk-2k4` — the one that needs a receiver to judge.

## Why these

Every wrong conclusion reached about this radio came from a probe that could not
distinguish the cases it was used to decide: first a symmetric comb, then a symmetric
two-tone pair. Both contain the tone that would explain either answer. Each entry here is
asymmetric in whatever dimension it is testing, so a run either matches the expected
result or contradicts it.
