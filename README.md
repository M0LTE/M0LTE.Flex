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

## Wideband IQ transmit (Waveform API)

Transmit arbitrary complex IQ through a custom waveform — the only way to put wideband IQ on air
on a 6000-series radio. `underlying_mode=RAW` carries both sidebands (true wideband complex);
`USB`/`IQ` are single-sideband.

```csharp
using M0LTE.Flex;

await using FlexClient client = await FlexClient.ConnectAsync("192.168.1.50");

await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
    client,
    new FlexWaveformOptions { Frequency = "14.100000", Antenna = "ANT1", UnderlyingMode = "RAW", RfPower = 10 });

using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
FlexPtt ptt = waveform.CreatePtt(confirmInterlock: true);

ptt.Key();                               // the radio starts pulling TX buffers from us
iq.Write(myComplexIq);                   // interleaved I, Q at FlexWaveformIqOutput.SampleRate (24 kHz)
iq.Drain(TimeSpan.FromSeconds(5));       // block until the burst has gone out…
ptt.Unkey();                             // …then release
```

The waveform is reflection-driven: while keyed, the radio streams TX buffers and
`FlexWaveformIqOutput` reflects your buffered IQ back for each one. Bandwidth is capped by the
24 kHz waveform rate (±12 kHz).

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
