# Provenance

`M0LTE.Flex` implements the FlexRadio 6000-series SmartSDR API subset needed to discover a
radio, run its command/status session, stream VITA-49 DAX audio in and out, and key a slice.
The wire protocol is publicly documented by FlexRadio Systems (the `smartsdr-api-docs` wiki:
*TCPIP-dax*, *TCPIP-stream*, *Discovery-protocol*, *TCPIP-CommandProtocol*). Several parts
are **ports with provenance** from the MIT-licensed Go reference clients listed below, with
the specific borrowing recorded per file in the source XML docs.

## Per-file lineage

| File | Upstream | What was ported |
| --- | --- | --- |
| `FlexClient.cs` | kc2g-flex-tools/flexclient — `client.go` | The prologue exchange, the `C<seq>\|<cmd>\n` command format and the monotonic sequence/await model. |
| `FlexDiscovery.cs` | kc2g-flex-tools/flexclient — `discovery.go`, `discovery_unix.go` | `Discover`/`discoveryRecv`/`discoveryMatch` — the OUI `0x001C2D` + packet-class `0xFFFF` gate and the `key=value` parse; binding `:4992` with `SO_REUSEPORT`. |
| `FlexStation.cs` | kc2g-flex-tools/nDAX — `bindClient`, `findSlice`, `enableDax` | The **attach** path (bind → find slice by letter → enable DAX). The **headless** path (GUI-register, create our own slice, tolerate the redundant self-bind) is this project's own. |
| `FlexPtt.cs` | kc2g-flex-tools/nCAT — `ptt.go` | The `slice set <n> tx=1`-then-`xmit 1/0` sequence and the `interlock state==TRANSMITTING` read. |
| `FlexAudioInput.cs` | kc2g-flex-tools/nDAX — `readPacketsBuffered` | The 16-slot reorder/loss-concealing jitter ring keyed by the VITA 4-bit packet count. |
| `FlexAudioOutput.cs` | kc2g-flex-tools/nDAX — `streamFromPulse` | The DAX-TX packet layout and the continuous modulo-16 packet counter (pacing here is off the sample clock, not nDAX's 1 ms/packet sleep). |
| `DaxStreamFormat.cs` | kc2g-flex-tools/nDAX — `main.go` | The two `audioCfg` branches: sample rate / samples-per-packet / bytes-per-sample / format / stream class for reduced- and full-bandwidth DAX. |
| `Vita49.cs` | hb9fxq/flexlib-go — `vita/vitahandler.go`, `vita/vitatypes.go`; kc2g-flex-tools/nDAX — `main.go` | `ParseVitaPreamble` field layout and bit masks; the class codes, OUI and `MAX_VITA_PACKET_SIZE`; the DAX-audio TX packet byte layout (`streamFromPulse` 28-byte header). |

The remaining files (`AudioIo.cs`, `WavFile.cs`, `MockFlexRadio.cs`) are this project's own.

## Upstream projects and licences

Both reference clients are distributed under the MIT licence. MIT permits redistribution
under a stronger copyleft licence provided the original notice is retained, so the derived
portions above ship here under this project's AGPL-3.0-or-later (see `LICENSE`) while the
upstream MIT notice is preserved below.

- **kc2g-flex-tools** — nDAX, nCAT, flexclient — <https://github.com/kc2g-flex-tools> —
  Copyright (c) Andrew Rodland (KC2G).
- **hb9fxq/flexlib-go** — <https://github.com/hb9fxq/flexlib-go> —
  Copyright (c) 2017 Frank Werner-Häcker (HB9FXQ).

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

FlexRadio, SmartSDR, FLEX-6000 and related marks are trademarks of FlexRadio Systems. This
project is an independent client and is not affiliated with or endorsed by FlexRadio Systems.
