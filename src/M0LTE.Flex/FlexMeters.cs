using System.Globalization;

namespace M0LTE.Flex;

/// <summary>One row of the radio's <c>meter list</c> metadata.</summary>
/// <param name="Id">The 16-bit meter identifier that ties a value in the extension packet to
/// this description.</param>
/// <param name="Source">Source identifier — <c>RAD</c> (radio-wide), <c>SLC</c> (slice),
/// <c>PAN</c> (panadapter), <c>TX-</c>, <c>COD-</c>.</param>
/// <param name="SourceOrdinal">Ordinal of the source (e.g. the slice number).</param>
/// <param name="Name">Short meter name, e.g. <c>SWR</c>, <c>FWDPWR</c>, <c>PATEMP</c>.</param>
/// <param name="Unit">Units string, which determines the fixed-point scaling.</param>
/// <param name="Low">Declared lower bound.</param>
/// <param name="High">Declared upper bound.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Fps">Declared update rate in Hz. <b>0 means "on change only"</b>, which is the
/// difference between a meter that streams continuously while keyed and one that may never
/// arrive during a short burst — so it has to be read, not assumed.</param>
public sealed record FlexMeterDescriptor(
    int Id, string Source, string SourceOrdinal, string Name, string Unit,
    double Low, double High, string Description, double Fps);

/// <summary>A decoded meter sample.</summary>
/// <param name="Descriptor">The meter's metadata.</param>
/// <param name="Raw">The raw two's-complement int16 straight off the wire, <b>always</b>
/// retained — a wrong scaling divisor is then obvious and correctable from a log rather than
/// silently believed.</param>
/// <param name="Value">The raw value scaled per <see cref="FlexMeterDescriptor.Unit"/>.</param>
/// <param name="UtcTime">When the sample was decoded.</param>
public sealed record FlexMeterReading(FlexMeterDescriptor Descriptor, short Raw, double Value, DateTime UtcTime);

/// <summary>
/// FlexRadio meter (SWR / forward power / reflected power / PA temperature / supply volts)
/// telemetry over an existing <see cref="FlexClient"/> session.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The transmitter feeds a dummy load and its own receiver is
/// blanked while keyed, so there is <em>no</em> self-check on the transmit side
/// (ota-execution-plan §T4). Meters are the only transmitter-health signal available, and the
/// pre-flight SWR check is what catches "the dummy load is not actually connected" before
/// power goes into an open antenna port.</para>
/// <para><b>Provenance.</b> Wire format transcribed from the official FlexRadio API wiki
/// (<c>git clone https://github.com/flexradio/smartsdr-api-docs.wiki.git</c>), pages
/// <i>Metering-Protocol</i> and <i>TCPIP-meter</i>:</para>
/// <list type="bullet">
/// <item><description><c>meter list</c> returns the whole metadata table in the command
/// <b>result message</b> as <c>#</c>-separated <c>&lt;id&gt;.&lt;field&gt;=&lt;value&gt;</c>
/// pairs (fields <c>src num nam low hi desc unit fps peak</c>).</description></item>
/// <item><description><c>sub meter all</c> subscribes; values then arrive as VITA-49
/// extension packets with packet class <see cref="Vita49.MeterClass"/> (0x8002).</description></item>
/// <item><description>The payload is a sequence of 32-bit words, <b>upper 16 bits = meter
/// identifier, lower 16 bits = the value</b> (big-endian, like every other Flex
/// stream).</description></item>
/// <item><description>Scaling by unit (<i>TCPIP-meter</i> § Data Scaling): DB/DBM/DBFS ÷128,
/// VOLTS/AMPS ÷256, TEMPC/TEMPF ÷64, everything else used directly.</description></item>
/// </list>
/// <para><b>The SWR scaling is genuinely ambiguous in the documentation</b> and must not be
/// trusted blind — see <see cref="SwrFromPowers"/> for how the interlock avoids depending on
/// it. The two wiki pages disagree about VOLTS too (<i>Metering-Protocol</i> says ten bits
/// below the radix, i.e. ÷1024; <i>TCPIP-meter</i> says ÷256). The declared bounds settle it:
/// <c>+13.8A</c> declares low=10.5/hi=15.0, which only ÷256 can produce — so ÷256 is
/// implemented and the disagreement is recorded here rather than lost.</para>
/// <para><b>Verified against a FLEX-6500</b> (firmware v1.4.0.0, API V1.4.0.0) into a dummy
/// load: <c>SWR</c> raw 128 → 1.00, <c>+13.8A</c> raw 3294 → 12.87 V, <c>PATEMP</c> raw 2239 →
/// 34.98 °C, and <c>FWDPWR</c> 39.9 dBm from a commanded <c>rfpower=10</c> (9.7 W). Those three
/// readings are what pin the ÷128, ÷256 and ÷64 divisors to measurement rather than to a
/// documentation table that contradicts itself.</para>
/// </remarks>
public sealed class FlexMeters : IDisposable
{
    private readonly FlexClient _client;
    private readonly Dictionary<int, FlexMeterDescriptor> _descriptors = [];
    private readonly Dictionary<int, FlexMeterReading> _latest = [];
    private readonly Dictionary<int, long> _samplesById = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    private FlexMeters(FlexClient client) => _client = client;

    /// <summary>Raised for every decoded meter sample.</summary>
    public event Action<FlexMeterReading>? Updated;

    /// <summary>Meter extension packets decoded since subscription.</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>Per-meter <c>sub meter &lt;id&gt;</c> commands the radio rejected.</summary>
    public int SubscribeErrors { get; private set; }

    /// <summary>Values seen for meter identifiers we have no descriptor for. The wiki warns
    /// that metadata and values can be briefly out of sync, so these are dropped rather than
    /// guessed at — a persistently non-zero count means the metadata parse is wrong.</summary>
    public long UnknownIdSamples { get; private set; }

    /// <summary>How many values arrived for each meter identifier, <b>including ones with no
    /// descriptor</b>. This is the raw, un-interpreted truth about what the radio sent — the
    /// thing to look at when a meter that is described and declared at 20 fps never shows up,
    /// because it separates "the radio did not send it" from "we mislabelled it".</summary>
    public IReadOnlyDictionary<int, long> SamplesById
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<int, long>(_samplesById);
            }
        }
    }

    /// <summary>The parsed <c>meter list</c> metadata, by identifier.</summary>
    public IReadOnlyDictionary<int, FlexMeterDescriptor> Descriptors => _descriptors;

    /// <summary>An inert instance that never reports a reading — for radios (or mocks) with
    /// no meter surface. Callers get honest nulls rather than a fabricated interlock.</summary>
    public static FlexMeters None(FlexClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new FlexMeters(client) { _disposed = true };
    }

    /// <summary>Runs <c>meter list</c>, parses the metadata, subscribes to all meters and
    /// starts decoding value packets.</summary>
    public static async Task<FlexMeters> SubscribeAsync(FlexClient client, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var meters = new FlexMeters(client);

        FlexResult list = await client.SendCommandAsync("meter list", cancellation).ConfigureAwait(false);
        if (!list.IsOk)
        {
            throw new InvalidOperationException($"meter list failed: error 0x{list.Error:X8}");
        }

        foreach (FlexMeterDescriptor d in ParseMeterList(list.Message))
        {
            meters._descriptors[d.Id] = d;
        }

        client.VitaPacketReceived += meters.OnVitaPacket;
        await client.SendCommandExpectOkAsync("sub meter all", cancellation).ConfigureAwait(false);

        // …and then per-meter as well. On firmware V1.4.0.0 `sub meter all` alone was observed
        // to stream only part of the described set — the hardware transmit meters (FWDPWR,
        // REFPWR, SWR, HWALC) never arrived despite being declared at 20 fps. The wiki
        // documents `sub meter <index>` as a first-class alternative, so subscribing to each
        // described meter by id costs one command apiece and removes the doubt.
        foreach (int id in meters._descriptors.Keys)
        {
            FlexResult r = await client.SendCommandAsync(
                string.Create(CultureInfo.InvariantCulture, $"sub meter {id}"), cancellation)
                .ConfigureAwait(false);
            if (!r.IsOk)
            {
                meters.SubscribeErrors++;
            }
        }

        return meters;
    }

    /// <summary>
    /// Parses the <c>meter list</c> result message: an optional <c>meter </c> prefix followed
    /// by <c>#</c>-separated <c>&lt;id&gt;.&lt;field&gt;=&lt;value&gt;</c> pairs.
    /// </summary>
    /// <remarks>Split on the <em>first</em> '.' then the <em>first</em> '=': meter names and
    /// values legitimately contain both (<c>13.nam=+13.8A</c>, <c>1.low=-150.0</c>).</remarks>
    public static IReadOnlyList<FlexMeterDescriptor> ParseMeterList(string? message)
    {
        var fields = new Dictionary<int, Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        string body = message.Trim();
        if (body.StartsWith("meter ", StringComparison.OrdinalIgnoreCase))
        {
            body = body["meter ".Length..];
        }

        foreach (string token in body.Split('#', StringSplitOptions.RemoveEmptyEntries))
        {
            int dot = token.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0)
            {
                continue;
            }

            if (!int.TryParse(token[..dot], out int id))
            {
                continue;
            }

            int eq = token.IndexOf('=', dot);
            if (eq < 0)
            {
                continue;
            }

            string field = token[(dot + 1)..eq].Trim();
            string value = token[(eq + 1)..].Trim().Trim('"');
            if (!fields.TryGetValue(id, out Dictionary<string, string>? kv))
            {
                kv = [];
                fields[id] = kv;
            }

            kv[field] = value;
        }

        var result = new List<FlexMeterDescriptor>(fields.Count);
        foreach ((int id, Dictionary<string, string> kv) in fields)
        {
            result.Add(new FlexMeterDescriptor(
                id,
                Get(kv, "src"),
                Get(kv, "num"),
                Get(kv, "nam"),
                Get(kv, "unit"),
                Num(kv, "low"),
                Num(kv, "hi"),
                Get(kv, "desc"),
                Num(kv, "fps")));
        }

        result.Sort((a, b) => a.Id.CompareTo(b.Id));
        return result;

        static string Get(Dictionary<string, string> kv, string k) => kv.TryGetValue(k, out string? v) ? v : "";
        static double Num(Dictionary<string, string> kv, string k) =>
            kv.TryGetValue(k, out string? v) && double.TryParse(v, out double d) ? d : double.NaN;
    }

    /// <summary>
    /// Decodes a meter extension-packet payload into (identifier, raw value) pairs:
    /// big-endian 32-bit words, identifier in the top half, two's-complement value in the
    /// bottom half.
    /// </summary>
    public static List<(int Id, short Raw)> DecodePayload(ReadOnlySpan<byte> payload)
    {
        var values = new List<(int, short)>(payload.Length / 4);
        for (int i = 0; i + 3 < payload.Length; i += 4)
        {
            int id = (payload[i] << 8) | payload[i + 1];
            short raw = (short)((payload[i + 2] << 8) | payload[i + 3]);
            values.Add((id, raw));
        }

        return values;
    }

    /// <summary>Applies the unit's fixed-point scaling (see the class remarks for provenance
    /// and for the documented ambiguities).</summary>
    public static double Scale(short raw, string unit)
    {
        return (unit ?? "").ToUpperInvariant() switch
        {
            // SWR is ÷128 here on the authority of the MIT reference client
            // (kc2g-flex-tools/flexclient, meter.go translateMeterValue: "dBm", "dBFS", "dB",
            // "SWR" all ÷128), NOT the wiki's "all others used directly" — which would give
            // integer-only SWR and cannot resolve 1.0 from 1.5. The wiki's rule is written for
            // meters a *client* creates and sends, not for reading the radio's own.
            "DB" or "DBM" or "DBFS" or "SWR" => raw / 128.0,
            "VOLTS" or "AMPS" => raw / 256.0,
            "TEMPC" or "TEMPF" or "DEGC" or "DEGF" => raw / 64.0,
            _ => raw,
        };
    }

    /// <summary>Most recent sample of a meter by name, preferring a transmit-side source when
    /// the name is ambiguous.</summary>
    public bool TryGet(string name, out FlexMeterReading reading)
    {
        lock (_gate)
        {
            FlexMeterReading? best = null;
            foreach (FlexMeterReading r in _latest.Values)
            {
                if (!string.Equals(r.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best is null || (r.Descriptor.Source.StartsWith("TX", StringComparison.OrdinalIgnoreCase)
                        && !best.Descriptor.Source.StartsWith("TX", StringComparison.OrdinalIgnoreCase)))
                {
                    best = r;
                }
            }

            reading = best!;
            return best is not null;
        }
    }

    /// <summary>A snapshot of every meter's latest sample.</summary>
    public IReadOnlyList<FlexMeterReading> Snapshot()
    {
        lock (_gate)
        {
            return [.. _latest.Values];
        }
    }

    /// <summary>
    /// SWR computed from forward and reflected power rather than read from the SWR meter.
    /// </summary>
    /// <remarks>
    /// This is the value the safety interlock should trust. <c>FWDPWR</c> and <c>REFPWR</c>
    /// are both <c>dBm</c>, whose ÷128 scaling is unambiguous in the documentation <em>and</em>
    /// independently anchored (forward power must track commanded <c>rfpower</c>), whereas the
    /// SWR meter's own scaling is not clearly specified — the <i>Metering-Protocol</i> units
    /// table omits SWR entirely and <i>TCPIP-meter</i>'s "all others used directly" would give
    /// integer-only SWR. Computing it here from two well-defined meters, and reporting the SWR
    /// meter alongside as a cross-check, means the first bench session settles the question
    /// instead of depending on the answer.
    /// <para>Γ = 10^((P_ref − P_fwd)/20), SWR = (1+Γ)/(1−Γ). Returns null when forward power
    /// is below <paramref name="minForwardDbm"/> (no meaningful transmission — the ratio is
    /// then noise) or when reflected ≥ forward.</para>
    /// </remarks>
    public double? SwrFromPowers(double minForwardDbm = 20.0)
    {
        if (!TryGet("FWDPWR", out FlexMeterReading fwd) || !TryGet("REFPWR", out FlexMeterReading rev))
        {
            return null;
        }

        if (fwd.Value < minForwardDbm)
        {
            return null;
        }

        double gamma = Math.Pow(10.0, (rev.Value - fwd.Value) / 20.0);
        if (gamma >= 1.0)
        {
            return double.PositiveInfinity;
        }

        return (1.0 + gamma) / (1.0 - gamma);
    }

    /// <summary>Converts a dBm reading to watts — <c>FWDPWR</c>/<c>REFPWR</c> are reported in
    /// dBm (the 6500's declared range tops out at 53 dBm ≈ 200 W).</summary>
    public static double DbmToWatts(double dbm) => Math.Pow(10.0, (dbm - 30.0) / 10.0);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.VitaPacketReceived -= OnVitaPacket;
        try
        {
            _client.SendCommandNoWait("unsub meter all");
        }
        catch (ObjectDisposedException)
        {
            // session already gone — nothing to unsubscribe from
        }
        catch (IOException)
        {
        }
    }

    private void OnVitaPacket(VitaPacket packet)
    {
        if (packet.PacketClassCode != Vita49.MeterClass)
        {
            return;
        }

        // packet.Payload is the payload — see VitaPacket's remarks for why this type exists
        // and why re-applying PayloadOffset here is the bug it was created to prevent.
        ReadOnlySpan<byte> payload = packet.Payload.Span;
        DateTime now = DateTime.UtcNow;
        var fired = new List<FlexMeterReading>();

        lock (_gate)
        {
            PacketsReceived++;
            foreach ((int id, short raw) in DecodePayload(payload))
            {
                _samplesById[id] = _samplesById.TryGetValue(id, out long n) ? n + 1 : 1;
                if (!_descriptors.TryGetValue(id, out FlexMeterDescriptor? d))
                {
                    UnknownIdSamples++;
                    continue;
                }

                var reading = new FlexMeterReading(d, raw, Scale(raw, d.Unit), now);
                _latest[id] = reading;
                fired.Add(reading);
            }
        }

        Action<FlexMeterReading>? handler = Updated;
        if (handler is null)
        {
            return;
        }

        foreach (FlexMeterReading r in fired)
        {
            handler(r);
        }
    }
}
