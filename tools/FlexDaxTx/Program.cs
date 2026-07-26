using System.Globalization;
using M0LTE.Flex;

namespace M0LTE.Flex.Tools.DaxTx;

/// <summary>
/// Transmits mono audio from stdin through a FlexRadio <b>DAX audio</b> stream.
/// </summary>
/// <remarks>
/// The other transmit path, and a very different one from the waveform. DAX carries <b>real audio</b>
/// into an ordinary slice, so the signal is whatever that slice's mode makes of it — upper sideband
/// for USB/DIGU, lower for LSB/DIGL. None of the waveform path's sideband and placement mechanics
/// apply here: this is a sound card, not an IQ injector. Bandwidth is set by the radio's transmit
/// filter, measured at up to 10 kHz — <b>not</b> the ~3 kHz this path is usually assumed to have.
/// </remarks>
internal static class Program
{
    private const int BlockSamples = 4800;                 // 100 ms at 48 kHz
    private static volatile bool _aborted;
    private static volatile bool _keyed;

    private const string Usage = """
        flex-dax-tx — transmit mono audio from stdin through a FlexRadio DAX audio stream.

        USAGE
          <source of audio> | flex-dax-tx --radio <ip> --freq <MHz> [--mode DIGU]

        DAX carries REAL AUDIO into an ordinary slice, so a 1 kHz tone lands 1 kHz above the dial
        in DIGU/USB and 1 kHz below it in DIGL/LSB. This is the sound-card path — the waveform
        path's sideband and placement rules do not apply.

        Bandwidth is set by the radio's TRANSMIT FILTER, not by the slice: measured on a 6500, an
        audio sweep was cut at exactly 10 kHz with the filter at 10000 and at exactly 3 kHz with it
        at 3000. DAX is not a ~3 kHz path — it carries whatever that filter allows, up to the same
        10 kHz ceiling the waveform path has. It is a global setting, so it is reported on every run
        and only changed if you pass --bw.

          flex-iq-gen tone --offset 1000 --real | flex-dax-tx --radio 10.45.0.76 --freq 14.100
          sox voice.wav -t raw -e float -b 32 -r 48000 -c 1 - | flex-dax-tx --radio … --freq 14.1
          flex-dax-tx --radio … --freq 14.100 --rate 24000 --format s16 --in tone.s16

        OPTIONS
          --radio <spec>    radio IP/hostname, discovery spec, or "mock" (default: discover)
          --freq <MHz>      the slice's dial frequency; bare = MHz, or suffix k/M
          --mode <m>        slice mode (default: DIGU). DIGU/USB put audio above the dial,
                            DIGL/LSB below it
          --rate <hz>       DAX wire rate: 48000 (full-bandwidth float32, default) or 24000
                            (reduced-bandwidth s16). Your audio must already be at this rate
          --format <f>      stdin sample format: f32 (default) or s16. Independent of --rate
          --in <path>       read this file instead of stdin
          --ant <port>      antenna (default: ANT1)
          --dax-channel <n> DAX channel to claim (default: 1; a running SmartSDR takes 1)
          --power <watts>   TX power (default: 5)
          --bw <hz>         set the radio's transmit filter, which is what limits transmitted
                            audio bandwidth (clamps at 10000). Global and persistent; omit to
                            leave it as found, in which case the run reports what is in force
          --gain <x>        scale every sample before transmitting (default: 1.0)
          --max-seconds <n> stop after this much audio
          --dry-run         read and measure the audio, report, never key the radio
          --verbose         print the slice status after setup
          --help            this text
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
        TextWriter log = Console.Error;
        log.WriteLine("flex-dax-tx — mono audio through a FlexRadio DAX audio stream");
        log.WriteLine();
        log.WriteLine($"  input     {options.InputPath ?? "stdin"}, "
            + $"{options.Format.ToString().ToLowerInvariant()} mono at {options.RateHz} Hz");
        log.WriteLine($"  slice     {options.FreqMhz:F6} MHz {options.Mode} on {options.Antenna}, "
            + $"DAX channel {options.DaxChannel}");
        log.WriteLine($"  audio     lands {(options.Mode is "DIGL" or "LSB" ? "BELOW" : "ABOVE")} the dial "
            + $"({options.Mode}), limited by the radio's transmit filter");
        log.WriteLine($"  power     {options.PowerWatts:F0} W"
            + (options.Gain == 1 ? "" : $", input scaled x{options.Gain:F3}"));
        log.WriteLine();

        Stream input = options.InputPath is null
            ? Console.OpenStandardInput()
            : new FileStream(options.InputPath, FileMode.Open, FileAccess.Read);

        using (input)
        {
            var reader = new AudioReader(input, options.Format);
            var block = new float[BlockSamples];

            if (options.DryRun)
            {
                return DryRun(options, reader, block, log);
            }

            log.WriteLine($"connecting to {options.Radio} …");
            (FlexClient client, MockFlexRadio? mock) = await ConnectAsync(options).ConfigureAwait(false);

            FlexStation? station = null;
            try
            {
                station = await FlexStation.SetUpHeadlessAsync(
                    client,
                    options.StreamFormat,
                    new FlexStationOptions
                    {
                        Frequency = options.FreqMhz.ToString("F6", CultureInfo.InvariantCulture),
                        Antenna = options.Antenna,
                        SliceMode = options.Mode,
                        DaxChannel = options.DaxChannel,
                        TransmitFilterHighHz = options.TransmitFilterHighHz,
                    }).ConfigureAwait(false);

                log.WriteLine($"  slice {station.SliceIndex} up, DAX {options.StreamFormat.SampleRate} Hz "
                    + $"{(options.StreamFormat.IsReducedBandwidth ? "s16" : "float32")}");
                if (station.TuneWarning is string warning)
                {
                    log.WriteLine($"  warning: {warning}");
                }

                // The transmit filter is what truncates the audio, and it is global — so report what
                // is actually in force rather than letting a stale value shape the signal unseen.
                if (station.TransmitFilter is (int low, int high))
                {
                    log.WriteLine($"  audio bw  {low}-{high} Hz (the radio's transmit filter — this, not "
                        + "the slice, is the limit)");
                }

                if (station.TransmitSourceWarning is string sourceWarning)
                {
                    log.WriteLine($"  ← {sourceWarning}");
                }

                await client.SendCommandAsync(
                    $"transmit set rfpower={(int)Math.Round(options.PowerWatts)}").ConfigureAwait(false);

                await ProbeAsync(client, station, options, log).ConfigureAwait(false);

                if (options.NoTx)
                {
                    log.WriteLine();
                    log.WriteLine("  --no-tx: set up and probed, nothing keyed");
                    return 0;
                }

                FlexAudioOutput output = station.CreateAudioOutput();
                FlexPtt ptt = station.CreatePtt(confirmInterlock: true);

                Stats stats = Stream(options, reader, output, ptt, block, log);

                log.WriteLine();
                log.WriteLine($"  sent      {reader.SamplesRead:N0} samples "
                    + $"({reader.SamplesRead / (double)options.RateHz:F2} s), {output.PacketsSent:N0} DAX packets");
                log.WriteLine($"  peak      {stats.Peak:F4}"
                    + (stats.Clipped == 0 ? "" : $"   clipped {stats.Clipped:N0}  ← LOWER --gain"));

                return stats.Clipped == 0 && !_aborted ? 0 : 1;
            }
            finally
            {
                if (station is not null)
                {
                    await station.DisposeAsync().ConfigureAwait(false);
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
    }

    /// <summary>Prints the transmitter and slice state, and runs any raw probe commands.</summary>
    private static async Task ProbeAsync(FlexClient client, FlexStation station, Options options, TextWriter log)
    {
        await client.SendCommandAsync("sub tx all").ConfigureAwait(false);

        foreach (string command in options.PostCommands)
        {
            FlexResult result = await client.SendCommandAsync(command).ConfigureAwait(false);
            log.WriteLine($"  probe      {command}   → err=0x{result.Error:X8}"
                + (result.Message.Length > 0 ? $" \"{result.Message}\"" : "")
                + (result.IsOk ? "" : "   ← REJECTED"));
        }

        await Task.Delay(400).ConfigureAwait(false);
        Dump(client, "transmit", ["dax", "mic_selection", "mic_level", "lo", "hi", "rfpower", "inhibit", "tune"], log);
        Dump(client, "slice " + station.SliceIndex, ["mode", "RF_frequency", "tx", "dax", "dax_tx", "dax_clients", "txant"], log);

        if (options.Verbose)
        {
            DumpAll(client, "transmit", log);
            DumpAll(client, "slice " + station.SliceIndex, log);
        }
    }

    private static void Dump(FlexClient client, string prefix, string[] keys, TextWriter log)
    {
        if (!client.TryFindObject(prefix, _ => true, out string name)
            || !client.TryGetObject(name, out IReadOnlyDictionary<string, string> state))
        {
            log.WriteLine($"  {prefix,-9}  (no status reported)");
            return;
        }

        var parts = new List<string>();
        foreach (string key in keys)
        {
            if (state.TryGetValue(key, out string? value))
            {
                parts.Add($"{key}={value}");
            }
        }

        log.WriteLine($"  {prefix.Split(' ')[0],-9}  {string.Join(" ", parts)}");
    }

    private static void DumpAll(FlexClient client, string prefix, TextWriter log)
    {
        if (client.TryFindObject(prefix, _ => true, out string name)
            && client.TryGetObject(name, out IReadOnlyDictionary<string, string> state))
        {
            log.WriteLine($"  --- {name} ---");
            foreach (KeyValuePair<string, string> pair in state)
            {
                log.WriteLine($"      {pair.Key}={pair.Value}");
            }
        }
    }

    private sealed record Stats(double Peak, long Clipped);

    /// <summary>
    /// Keys, streams the audio, drains and unkeys.
    /// </summary>
    /// <remarks>
    /// Unlike the waveform path, DAX TX is <b>push</b>: nothing pulls buffers from us, so
    /// <see cref="FlexAudioOutput"/> paces itself against the sample clock and the loop simply feeds
    /// it. Keying first matters for the same reason as ever — audio sent before the PA is up does not
    /// go anywhere.
    /// </remarks>
    private static Stats Stream(
        Options options, AudioReader reader, FlexAudioOutput output, FlexPtt ptt, float[] block, TextWriter log)
    {
        long limit = options.MaxSeconds is double seconds
            ? (long)(seconds * options.RateHz)
            : long.MaxValue;

        double peak = 0;
        long clipped = 0;

        int Fill()
        {
            if (reader.SamplesRead >= limit)
            {
                return 0;
            }

            int got = reader.Read(block);
            for (int i = 0; i < got; i++)
            {
                double value = block[i] * options.Gain;
                peak = Math.Max(peak, Math.Abs(value));
                if (value is > 1 or < -1)
                {
                    clipped++;
                    value = Math.Clamp(value, -1, 1);
                }

                block[i] = (float)value;
            }

            return got;
        }

        int first = Fill();
        if (first == 0)
        {
            log.WriteLine("  (no audio on stdin — nothing to transmit)");
            return new Stats(0, 0);
        }

        log.WriteLine("keying …");
        _keyed = true;
        ptt.Key();

        try
        {
            output.Write(block.AsSpan(0, first));
            int got;
            while (!_aborted && (got = Fill()) > 0)
            {
                output.Write(block.AsSpan(0, got));
                if (output.PacketsSent % 100 == 0)
                {
                    log.Write($"\r  transmitting {reader.SamplesRead / (double)options.RateHz,7:F1} s   ");
                }
            }

            output.Drain();
            log.WriteLine();
            return new Stats(peak, clipped);
        }
        finally
        {
            if (_keyed)
            {
                ptt.Unkey();
            }
        }
    }

    private static int DryRun(Options options, AudioReader reader, float[] block, TextWriter log)
    {
        log.WriteLine("dry run — reading the audio, not keying the radio");
        double peak = 0;
        double sumSquares = 0;
        int got;
        while ((got = reader.Read(block)) > 0)
        {
            for (int i = 0; i < got; i++)
            {
                double value = block[i] * options.Gain;
                peak = Math.Max(peak, Math.Abs(value));
                sumSquares += value * value;
            }
        }

        if (reader.SamplesRead == 0)
        {
            Console.Error.WriteLine("error: no audio on stdin");
            return 2;
        }

        double rms = Math.Sqrt(sumSquares / reader.SamplesRead);
        log.WriteLine();
        log.WriteLine($"  samples   {reader.SamplesRead:N0} mono "
            + $"({reader.SamplesRead / (double)options.RateHz:F2} s at {options.RateHz} Hz)");
        log.WriteLine($"  rms       {rms:F4}");
        log.WriteLine($"  peak      {peak:F4}   →  crest {20 * Math.Log10(peak / Math.Max(rms, 1e-9)):F1} dB");
        if (peak > 1)
        {
            log.WriteLine("  ← PEAK EXCEEDS FULL SCALE: this will clip. Lower --gain.");
        }

        return peak > 1 ? 1 : 0;
    }

    private static async Task<(FlexClient Client, MockFlexRadio? Mock)> ConnectAsync(Options options)
    {
        if (options.Radio.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            var mock = new MockFlexRadio(options.StreamFormat, MockRxMode.Silence);
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
