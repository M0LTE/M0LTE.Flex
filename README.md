# M0LTE.Flex

A dependency-free .NET client for **FlexRadio 6000-series** SDRs over the SmartSDR TCP/UDP
API. It gives you the radio as three simple seams — **audio in**, **audio out**, and **PTT** —
so a modem, a paging encoder, or any DSP can drive a Flex the way it would drive a sound card,
plus the discovery, command/status session and VITA-49 DAX plumbing underneath.

```
┌──────────────┐   discover / connect      ┌───────────────────────────┐
│  your app    │ ────────────────────────▶│  FlexClient  (TCP :4992)  │
│ (modem, DSP, │   IAudioInput  (RX) ◀────│  FlexStation (slice+DAX)  │──▶ FLEX-6000
│  paging, …)  │   IAudioOutput (TX) ────▶│  VITA-49 DAX  (UDP :4991) │
│              │   IPttControl  ─────────▶│  slice PTT (xmit 1/0)     │
└──────────────┘                           └───────────────────────────┘
```

- **Targets** `net10.0`. One dependency: [`M0LTE.Radio.Audio`](https://www.nuget.org/packages/M0LTE.Radio.Audio) — the shared `IAudioInput`/`IAudioOutput`/`IPttControl` seam.
- **Two bring-up modes**: *headless* (this client creates its own slice — no SmartSDR
  running) and *attach* (bind a slice a running SmartSDR already owns).
- **Both DAX transports**: reduced-bandwidth 24 kHz s16 and full-bandwidth 48 kHz float32.
- **Hardware-free tests**: an in-process `MockFlexRadio` speaks enough of the protocol to
  loop transmit audio back as receive audio.

## Install

```sh
dotnet add package M0LTE.Flex
```

## Quick start

```csharp
using M0LTE.Flex;

// 1. Find a radio on the LAN and open the session (or FlexClient.ConnectAsync("192.168.1.50")).
await using FlexClient client = await FlexClient.DiscoverAndConnectAsync(
    spec: null, timeout: TimeSpan.FromSeconds(10));

// 2. Pick a DAX transport for your sample rate, then bring up a slice + DAX streams.
//    SetUpHeadlessAsync creates our own slice; SetUpAsync attaches to a running SmartSDR's.
DaxStreamFormat format = DaxStreamFormat.FullBandwidth;   // 48 kHz float32 (or .ReducedBandwidth for 24 kHz s16)
var options = new FlexStationOptions
{
    SliceLetter = "A",
    Frequency = "14.100000",
    SliceMode = "DIGU",
    DaxChannel = "1",
};
await using FlexStation station = await FlexStation.SetUpHeadlessAsync(client, format, options);

// 3. Receive, transmit and key through the seams (M0LTE.Radio.Audio).
using M0LTE.Radio.Audio;
IAudioInput  rx  = station.CreateAudioInput();
IAudioOutput tx  = station.CreateAudioOutput();
IPttControl  ptt = station.CreatePtt();

Span<float> buffer = new float[1024];
int got = rx.Read(buffer);          // normalised floats (−1..1) at format.SampleRate

ptt.Key();
tx.Write(mySamples);                // your modulated audio at format.SampleRate
tx.Drain();                         // block until the audio has left the radio…
ptt.Unkey();                        // …then release
```

`DaxStreamFormat.ForDspRate(rate)` picks the transport that bridges your DSP rate with an
integer ratio (48000 → full-bandwidth 1:1; 12000/24000 → reduced-bandwidth). The audio seams
always present samples at `format.SampleRate`; resample to your own rate on the way in/out.

## Wideband IQ receive (DAX-IQ)

Stream raw complex baseband from one slice — before the SSB filter and AGC — for a wideband
decoder or to fan several channels out of a single capture.

```csharp
using M0LTE.Flex;

await using FlexClient client = await FlexClient.ConnectAsync("192.168.1.50");

// A 96 kSPS DAX-IQ stream centred on 14.100 MHz (rates: 24/48/96/192 kSPS).
await using FlexDaxIqSource iq = await FlexDaxIqSource.OpenAsync(
    client,
    new FlexDaxIqOptions(FrequencyMHz: "14.100000", Antenna: "ANT1", DaxChannel: 2, RateKsps: 96),
    ownsClient: true);

// Read blocks of interleaved I, Q floats at iq.SampleRate (host-endian). Read blocks until data
// arrives, and returns 0 once disposed — pump it like a capture device.
Span<float> block = new float[8192];
int got = iq.Read(block);          // got floats = got / 2 complex samples
Console.WriteLine($"{iq.PacketsReceived} packets, {iq.PacketsLost} lost");
```

`FlexDaxIqSource` implements `IIqSource` (`SampleRate` / `CentreFrequencyHz` / `Read`), so a
digital-downconverter front end can fan it into several narrowband channels.

## IQ transmit (Waveform API)

Transmit arbitrary complex IQ through a custom waveform — the only way to put IQ on air on a
6000-series radio.

**Say where the signal goes and how wide it is, and the library places it:**

```csharp
using M0LTE.Flex;

await using FlexClient client = await FlexClient.ConnectAsync("192.168.1.50");

await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
{
    // Where the signal goes, how wide it is, and which convention you write in.
    Band     = new IqBand(14.200000, 3000, IqBandReference.LowerEdge),
    RfPower  = 5,
});

using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
FlexPtt ptt = waveform.CreatePtt(confirmInterlock: true);

ptt.Key();                               // the radio starts pulling TX buffers from us
iq.Write(myComplexIq);                   // your baseband, 0 … +3000 Hz, at iq.SampleRate (24 kHz)
iq.Drain(TimeSpan.FromSeconds(5));
ptt.Unkey();

// On air: 14.200000 – 14.203000 MHz, spectrum upright.
```

`IqBand`'s reference names the convention you write in, and is the only thing you have to decide:

| `IqBandReference` | You supply | `FrequencyMhz` means |
|---|---|---|
| `Centre` (default) | DC-centred baseband, `−bw/2 … +bw/2` — the usual SDR convention (GNU Radio, SoapySDR, UHD, SigMF) | the **centre** of the band |
| `LowerEdge` | one-sided baseband, `0 … +bw` | the **lower edge** of the band |

The library then works out everything the radio makes awkward, and reports what it did on
`SliceFrequencyMhz`, `BasebandShiftHz`, `OccupiedBand` and `TransmitFilter`.

### Why that is worth having

Three properties of the transmit path all report `err=0` and are invisible without an external
receiver (measured on a FLEX-6500, firmware 4.1.5, 2026-07-26):

- **It is single-sideband, and only the negative half survives.** Every `underlying_mode` transmits
  only the **negative** half of your baseband — positive-frequency content never reaches the air.
  What differs is which side of the carrier it lands on, and hence whether your spectrum arrives
  upright: `RAW`/`LSB`/`DIGL` place `−f` at `slice − f` (**upright**), while `IQ`/`USB`/`DIGU` place
  it at `slice + f` (**mirrored**). `AM`/`FM` discard Q entirely. A band left straddling the carrier
  loses half its width.
- **The transmit filter caps the rest.** It defaults to a 3 kHz SSB passband and clamps at 10 kHz, so
  the usable width is about **10 kHz one-sided**. It is set with `filter_low=`/`filter_high=` but
  *reported* as `lo`/`hi` — `transmit set hi=` is rejected. It is also a **global** radio setting that
  persists and affects ordinary SSB (factory value 0–3000 Hz).
- **The waveform's own `tx_filter` does nothing.** `TxFilterLowHz`/`TxFilterHighHz` are accepted with
  `err=0` and have no measurable effect on this firmware.
- **The rate is fixed at 24 kHz complex and cannot be raised.** The firmware's own usage string for
  `waveform set` enumerates every parameter it takes — `rx_filter|tx_filter|tx|logging|udpport` — and
  no rate is among them; `slice set <n> sample_rate=` is accepted with `err=0` and ignored, the slice
  still reporting `sample_rate=24000`. The measured cadence agrees: 128 complex samples pulled 187.5
  times a second, exactly 24 kSPS. It costs nothing, because the ~10 kHz transmit filter is the real
  limit and sits well inside the ±12 kHz that 24 kHz already gives. **Receive is a different path** —
  DAX-IQ runs at 24/48/96/192 kSPS and `FlexDaxIqSource` supports all four.

Band placement absorbs all three: it derives the slice frequency, frequency-shifts your samples into
the sideband that actually transmits, opens the transmit filter far enough, and **fails setup** rather
than putting a truncated signal on air if the radio cannot honour the width.

The shift is a true frequency translation, never a spectral mirror. Conjugating would land a one-sided
baseband on the other sideband just as neatly, and invert it — so a QPSK or OFDM signal would look
perfect on a spectrum analyser and decode nowhere.

### Placing the IQ yourself

Set `SliceFrequencyMhz` instead of `Band` and the slice tunes there with your samples going out
untouched — for replaying a capture verbatim, or moving a signal around within the band without the
dial shifting under you. You then own the sideband and filter problems above.

The two are **mutually exclusive and exactly one must be set**: which mode you are in is visible at
the call site rather than implied by whether some other property happens to be filled in, and there
is no default transmit frequency to be surprised by.

The waveform is reflection-driven either way: while keyed, the radio streams TX buffers and
`FlexWaveformIqOutput` reflects your buffered IQ back for each one.

## DAX audio transmit

`FlexStation` + `FlexAudioOutput` is the sound-card path: real mono audio into an ordinary slice,
landing above the dial in `DIGU`/`USB` and below it in `DIGL`/`LSB`.

**Measured on a FLEX-6500 (fw 4.1.5, 2026-07-26), two things that are easy to get wrong:**

- **The transmitter must be pointed at DAX** (`transmit set dax=1`). Creating the DAX streams and
  pushing packets into them is *not* enough — the transmitter has its own audio-source selection
  which defaults to the mic, and every command in the DAX enable returns `err=0` either way. A 1 kHz
  tone produced **no modulation at all** until this was sent. `SetUpHeadlessAsync` now does it and
  reads it back (`TransmitSourceIsDax`); set `SelectDaxAsTransmitSource = false` to decline.
- **Bandwidth is the transmit filter, not the slice.** An audio sweep was cut at *exactly* 10 kHz
  with the filter at 10000, and at *exactly* 3 kHz with it at 3000. **DAX is not a ~3 kHz path** — it
  carries whatever that filter allows, up to the same 10 kHz ceiling the waveform path has. It is a
  global setting, so the default is to leave it alone and report it on `TransmitFilter`; set
  `TransmitFilterHighHz` to change it.

Both settings persist after teardown and affect what the radio transmits from thereafter.

## Testing without a radio

```csharp
using M0LTE.Flex;

await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Loopback, MockSetupMode.Headless);
mock.Start();

await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
mock.RxDelivery = client.DeliverVitaPacket;   // deliver the mock's DAX in-process (lossless)
client.VitaSendHook = mock.DeliverTxPacket;    // capture what we transmit
```

See the test project for full loopback examples exercising the reorder ring, the headless and
attach bring-up sequences, and the VITA-49 codec.

## Stability & versioning

The public API is **locked by a build-time test** (`PublicApiTests` compares the surface to a
committed snapshot), and the package follows [Semantic Versioning](https://semver.org/). Any
change to the public surface shows up in the diff and must be paired with the right version
bump — see [`docs/versioning.md`](docs/versioning.md).

## Licence & provenance

AGPL-3.0-or-later (see [`LICENSE`](LICENSE)). Parts of the wire implementation are ports of
the MIT-licensed Go reference clients by Andrew Rodland (KC2G) and Frank Werner-Häcker
(HB9FXQ); the attributions are in [`PROVENANCE.md`](PROVENANCE.md). Not affiliated with or
endorsed by FlexRadio Systems.
