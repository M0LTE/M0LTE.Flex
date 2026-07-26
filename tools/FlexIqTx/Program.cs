using System.Globalization;
using M0LTE.Flex;

namespace M0LTE.Flex.Tools.IqTx;

/// <summary>
/// Transmits an arbitrary complex-baseband IQ stream from stdin through a FlexRadio waveform.
/// </summary>
/// <remarks>
/// <para>The composable half of the bench kit: anything that can write interleaved I/Q to a pipe can
/// now put a signal on air, without knowing anything about slices, sidebands or transmit filters.
/// Those are handed to <see cref="FlexWaveform"/>'s band placement, which uses <c>RAW</c> — the only
/// mode measured to carry complex IQ upright.</para>
/// <para>The stream is expected at <b>24 kHz complex</b>, the waveform rate. There is deliberately no
/// resampler: getting one wrong would distort the signal in ways this tool exists to rule out, and
/// resampling is exactly what the rest of a pipeline is good at.</para>
/// </remarks>
internal static class Program
{
    private const int SampleRate = FlexWaveformIqOutput.SampleRate;
    private const int PacketPairs = 128;
    private const int BlockPairs = 2400;                   // 100 ms per write
    private const string WaveformName = "IqTx";
    private const string WaveformMode = "NOIS";

    private static volatile bool _aborted;
    private static volatile bool _keyed;

    private const string Usage = """
        flex-iq-tx — transmit an arbitrary IQ stream from stdin through a FlexRadio waveform.

        USAGE
          <source of IQ> | flex-iq-tx --radio <ip> --freq <MHz> --bw <hz>

        Reads interleaved complex baseband at 24 kHz (the waveform rate) and transmits it via
        underlying_mode=RAW. You say where the signal goes and how wide it is; the library derives
        the slice frequency, shifts the samples into the half the radio actually transmits, opens
        the transmit filter, and refuses rather than truncating.

          flex-iq-gen noise --bw 3k | flex-iq-tx --radio 10.45.0.76 --freq 14.200 --bw 3k
          flex-iq-tx --radio 10.45.0.76 --freq 14.200 --bw 10k < noise-10k.cf32
          sox rec.wav -t raw -e float -b 32 - | flex-iq-tx --radio … --freq 14.2 --bw 3k

        OPTIONS
          --radio <spec>    radio IP/hostname, discovery spec, or "mock" (default: discover)
          --freq <MHz>      where the signal goes; bare = MHz, or suffix k/M
          --bw <hz>         how wide your signal is. Sets the transmit filter and the placement;
                            the radio caps this at 10000 Hz
          --reference <r>   what --freq names, and where your DC sits: "centre" (you supply
                            DC-centred IQ) or "loweredge" (you supply 0..bw). Default: centre
          --in <path>       read this file instead of stdin. A SigMF .sigmf-meta sidecar beside it
                            is read automatically: its datatype is used, and its sample rate is
                            CHECKED against the waveform's 24 kHz rather than assumed
          --format <f>      cf32 (interleaved float32 LE — default) or cs16
          --power <watts>   TX power (default: 5)
          --ant <port>      antenna (default: ANT1)
          --gain <x>        scale every sample by this before transmitting (default: 1.0)
          --max-seconds <n> stop after this much audio, however long the stream is
          --raw             send the samples exactly as supplied at --freq, with no placement or
                            shift — for a stream already positioned for the radio, such as the
                            test corpus. Only its content below DC will transmit
          --dry-run         read and measure the stream, report, never key the radio
          --verbose         dump the radio's slice status after setup
          --help            this text

        The stream must already be at 24 kHz complex. There is no resampler — resample upstream,
        where the tooling for it already exists.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, a => a is "--help" or "-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("run with --help for usage");
            return 2;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _aborted = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("interrupted — unkeying");
        };

        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (FlexProtocolException ex)
        {
            Console.Error.WriteLine($"radio error: {ex.Message}");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"i/o error: {ex.Message}");
            return 1;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        // Everything goes to stderr, so stdout stays free for a future --tee without corrupting it.
        TextWriter log = Console.Error;
        log.WriteLine("flex-iq-tx — arbitrary IQ through a FlexRadio waveform");
        log.WriteLine();
        options = ApplySidecar(options, log);
        log.WriteLine($"  input     {options.InputPath ?? "stdin"}, "
            + $"{options.Format.ToString().ToLowerInvariant()} at {SampleRate} Hz complex");
        log.WriteLine(options.Raw
            ? $"  slice     {options.FreqMhz:F6} MHz, samples sent verbatim (--raw); content below DC "
                + $"reaches the air, filter set to {options.BandwidthHz:N0} Hz"
            : $"  band      {options.LowMhz:F6} – {options.HighMhz:F6} MHz   "
                + $"({options.BandwidthHz:N0} Hz wide, {(options.Reference == IqBandReference.LowerEdge ? "lower-edge" : "centre")}-referenced)");
        log.WriteLine($"  power     {options.PowerWatts:F0} W set" + (options.Gain == 1 ? "" : $", input scaled x{options.Gain:F3}"));
        log.WriteLine();

        Stream input = options.InputPath is null
            ? Console.OpenStandardInput()
            : new FileStream(options.InputPath, FileMode.Open, FileAccess.Read);

        using (input)
        {
            var reader = new IqReader(input, options.Format);
            var block = new float[BlockPairs * 2];
            return await RunWithInputAsync(options, reader, block, log).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Believes a SigMF sidecar over the command line, and refuses a file whose sample rate is not
    /// the waveform's.
    /// </summary>
    /// <remarks>
    /// The rate check is the reason this exists. Transmitting a 48 kHz capture as though it were
    /// 24 kHz produces a signal at half the intended width, with every frequency in it halved, and
    /// nothing anywhere says so — the file plays, the radio keys, the spectrum looks like a signal.
    /// A sidecar turns that from an invisible error into a refusal.
    /// </remarks>
    private static Options ApplySidecar(Options options, TextWriter log)
    {
        if (options.InputPath is null || SigMfMeta.FindBeside(options.InputPath) is not string metaPath)
        {
            return options;
        }

        SigMfMeta meta = SigMfMeta.Read(metaPath);
        log.WriteLine($"  sigmf     {Path.GetFileName(metaPath)}: {SigMfDataType(meta.Format)}, "
            + $"{meta.SampleRate} Hz"
            + (meta.Description is null ? "" : $"  \"{meta.Description}\""));

        if (meta.SampleRate != SampleRate)
        {
            throw new ArgumentException(
                $"{metaPath} declares {meta.SampleRate} Hz, but the waveform runs at {SampleRate} Hz. "
                + $"Transmitting it anyway would scale every frequency in the signal by "
                + $"{SampleRate / (double)meta.SampleRate:F3}x and change its width to match — resample "
                + "upstream first");
        }

        if (options.FormatSpecified && options.Format != meta.Format)
        {
            log.WriteLine($"  ← --format says {options.Format.ToString().ToLowerInvariant()} but the "
                + "sidecar says otherwise; believing the sidecar");
        }

        return options with { Format = meta.Format };
    }

    private static string SigMfDataType(IqFormat format) => format == IqFormat.Cf32 ? "cf32_le" : "ci16_le";

    private static async Task<int> RunWithInputAsync(
        Options options, IqReader reader, float[] block, TextWriter log)
    {

        if (options.DryRun)
        {
            return DryRun(options, reader, block, log);
        }

        log.WriteLine($"connecting to {options.Radio} …");
        (FlexClient client, MockFlexRadio? mock) = await ConnectAsync(options).ConfigureAwait(false);

        FlexWaveform? waveform = null;
        try
        {
            waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Name = WaveformName,
                Mode = WaveformMode,
                UnderlyingMode = "RAW",
                Frequency = options.FreqMhz.ToString("F6", CultureInfo.InvariantCulture),
                Antenna = options.Antenna,
                OccupiedBandwidthHz = options.Raw ? null : (int)Math.Round(options.BandwidthHz),
                BandReference = options.Reference,
                TransmitFilterHighHz = (int)Math.Round(options.BandwidthHz),
                RfPower = (int)Math.Round(options.PowerWatts),
            }).ConfigureAwait(false);

            if (waveform.OccupiedBand is (double placedLow, double placedHigh))
            {
                log.WriteLine($"  placed    {placedLow:F6} – {placedHigh:F6} MHz"
                    + $"   slice {waveform.SliceFrequencyMhz:F6} MHz   shift {waveform.BasebandShiftHz:+#;-#;0} Hz");
            }
            else
            {
                log.WriteLine($"  verbatim  slice {waveform.SliceFrequencyMhz:F6} MHz, samples sent unshifted"
                    + "   (only content below DC will transmit)");
            }
            if (waveform.TransmitFilter is (int low, int high))
            {
                log.WriteLine($"  tx filter {low}–{high} Hz from the carrier");
            }

            using FlexWaveformIqOutput iq = waveform.CreateIqOutput(bufferSeconds: 2.0);
            FlexPtt ptt = waveform.CreatePtt(confirmInterlock: true);

            Stats stats = await StreamAsync(options, reader, iq, ptt, block, log).ConfigureAwait(false);

            log.WriteLine();
            log.WriteLine($"  sent      {reader.SamplesRead:N0} complex samples "
                + $"({reader.SamplesRead / (double)SampleRate:F2} s)");
            log.WriteLine($"  reflected {iq.PacketsReflected:N0} TX buffers");
            log.WriteLine($"  starved   {stats.Starved:N0}"
                + (stats.Starved == 0 ? "" : "  ← GAPS IN THE SIGNAL"));
            log.WriteLine($"  peak      {stats.Peak:F4}"
                + (stats.Clipped == 0 ? "" : $"   clipped {stats.Clipped:N0}  ← LOWER --gain"));

            return stats.Starved == 0 && stats.Clipped == 0 && !_aborted ? 0 : 1;
        }
        finally
        {
            if (waveform is not null)
            {
                await waveform.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            if (mock is not null)
            {
                await mock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed record Stats(long Starved, double Peak, long Clipped);

    /// <summary>
    /// Queues a second of the stream before keying, then streams the rest under the radio's
    /// back-pressure.
    /// </summary>
    /// <remarks>
    /// The pre-key queue matters: the sink only drains while the interlock says we are transmitting,
    /// so filling it first is what makes the first moment of RF carry signal rather than silence.
    /// </remarks>
    private static async Task<Stats> StreamAsync(
        Options options, IqReader reader, FlexWaveformIqOutput iq, FlexPtt ptt, float[] block, TextWriter log)
    {
        long limit = options.MaxSeconds is double seconds
            ? (long)(seconds * SampleRate)
            : long.MaxValue;

        double peak = 0;
        long clipped = 0;

        int Fill()
        {
            if (reader.SamplesRead >= limit)
            {
                return 0;
            }

            int floats = reader.Read(block);
            for (int i = 0; i < floats; i++)
            {
                double value = block[i] * options.Gain;
                peak = Math.Max(peak, Math.Abs(value));
                if (value > 1 || value < -1)
                {
                    clipped++;
                    value = Math.Clamp(value, -1, 1);
                }

                block[i] = (float)value;
            }

            return floats;
        }

        // Pre-key fill.
        long queued = 0;
        int got;
        while (queued < SampleRate && (got = Fill()) > 0)
        {
            iq.Write(block.AsSpan(0, got));
            queued += got / 2;
        }

        if (queued == 0)
        {
            log.WriteLine("  (stdin was empty — nothing to transmit)");
            return new Stats(0, 0, 0);
        }

        log.WriteLine("keying …");
        _keyed = true;
        ptt.Key();

        try
        {
            var producer = Task.Run(() =>
            {
                int floats;
                while (!_aborted && (floats = Fill()) > 0)
                {
                    iq.Write(block.AsSpan(0, floats));
                }
            });

            long lastPackets = -1;
            int stalled = 0;
            while (!producer.IsCompleted)
            {
                await Task.WhenAny(producer, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                long packets = iq.PacketsReflected;
                log.Write($"\r  transmitting {packets * PacketPairs / (double)SampleRate,7:F1} s   "
                    + $"read {reader.SamplesRead / (double)SampleRate,7:F1} s   ");

                // A radio that stops pulling would block the producer in Write() forever.
                stalled = packets == lastPackets ? stalled + 1 : 0;
                lastPackets = packets;
                if (stalled >= 20)
                {
                    log.WriteLine();
                    Console.Error.WriteLine("error: the radio stopped requesting transmit buffers — aborting");
                    _aborted = true;
                    iq.Dispose();
                    break;
                }
            }

            await producer.ConfigureAwait(false);
            log.WriteLine();

            // Pad the tail to a whole transmit buffer. The radio pulls a fixed 128 complex samples
            // at a time and zero-fills any shortfall, so a stream that is not a multiple of that
            // ends with a part-filled packet — counted as a starve, and a real if tiny
            // discontinuity. A file is rarely a whole number of packets long.
            if (!_aborted)
            {
                long remainder = reader.SamplesRead % PacketPairs;
                if (remainder != 0)
                {
                    int padPairs = (int)(PacketPairs - remainder);
                    iq.Write(new float[padPairs * 2]);
                }
            }

            if (!_aborted && !iq.Drain(TimeSpan.FromSeconds(15)))
            {
                Console.Error.WriteLine("warning: transmit buffer did not drain within 15 s");
            }

            // Read the counter before unkeying: the post-unkey flush always starves one packet.
            return new Stats(iq.SamplesStarved, peak, clipped);
        }
        finally
        {
            if (_keyed)
            {
                ptt.Unkey();
            }
        }
    }

    private static int DryRun(Options options, IqReader reader, float[] block, TextWriter log)
    {
        log.WriteLine("dry run — reading the stream, not keying the radio");
        double peak = 0;
        double sumSquares = 0;
        int floats;
        while ((floats = reader.Read(block)) > 0)
        {
            for (int i = 0; i < floats; i++)
            {
                double value = block[i] * options.Gain;
                peak = Math.Max(peak, Math.Abs(value));
                sumSquares += value * value;
            }
        }

        long samples = reader.SamplesRead;
        if (samples == 0)
        {
            Console.Error.WriteLine("error: stdin was empty");
            return 2;
        }

        double rms = Math.Sqrt(sumSquares / (2 * samples));
        log.WriteLine();
        log.WriteLine($"  samples   {samples:N0} complex ({samples / (double)SampleRate:F2} s at {SampleRate} Hz)");
        log.WriteLine($"  rms       {rms:F4} per component");
        log.WriteLine($"  peak      {peak:F4}   →  crest {20 * Math.Log10(peak / Math.Max(rms, 1e-9)):F1} dB");
        if (peak > 1)
        {
            log.WriteLine("  ← PEAK EXCEEDS FULL SCALE: this will clip and splatter. Lower --gain.");
        }

        return peak > 1 ? 1 : 0;
    }

    private static async Task<(FlexClient Client, MockFlexRadio? Mock)> ConnectAsync(Options options)
    {
        if (options.Radio.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
            mock.Start();
            return (await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort).ConfigureAwait(false), mock);
        }

        bool looksLikeHost = options.Radio.Contains('.', StringComparison.Ordinal)
            && !options.Radio.Contains('=', StringComparison.Ordinal);

        FlexClient client = looksLikeHost
            ? await FlexClient.ConnectAsync(options.Radio).ConfigureAwait(false)
            : await FlexClient.DiscoverAndConnectAsync(
                options.Radio is "discover" ? null : options.Radio, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return (client, null);
    }
}
