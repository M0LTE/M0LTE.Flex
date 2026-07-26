using System.Globalization;
using System.Text;

namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// A bench rig that transmits band-limited complex Gaussian noise through a FlexRadio 6000 via the
/// Waveform API, to characterise the radio's IQ transmit bandwidth. The noise is flat and sharply
/// bounded by construction, so any narrowing seen on a receiver belongs to the radio.
/// </summary>
internal static class Program
{
    private const int PacketPairs = 128;                    // complex samples per radio transmit buffer
    private const int BlockPairs = 2400;                    // 100 ms of complex samples per write
    private const int AnalysisFftSize = 4096;               // 5.9 Hz bins at 24 kHz
    private const int MaxAnalysisSeconds = 8;
    private const double SweepGapSeconds = 3;               // dead air between sweep segments
    private const string WaveformName = "IqNoise";
    private const string WaveformMode = "NOIS";

    private static volatile bool _aborted;

    /// <summary>Set the instant a key is attempted, so the unkey in the teardown path runs even if
    /// the burst throws between keying and returning. Unkeying when not keyed is harmless; leaving a
    /// PA up is not.</summary>
    private static volatile bool _keyed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || Array.Exists(args, a => a is "--help" or "-h" or "-?"))
        {
            Console.WriteLine(Options.Usage);
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

        InstallInterruptHandler();
        PrintPlan(options);

        try
        {
            if (options.DryRun)
            {
                return RunDryRun(options);
            }

            return options.IsSweep
                ? await RunSweepAsync(options).ConfigureAwait(false)
                : await RunTransmitAsync(options).ConfigureAwait(false);
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
            // Some checks can only run once the radio is up (e.g. explore needing a terminal), so
            // this reaches past argument parsing — report it rather than dumping a stack trace.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static void PrintPlan(Options options)
    {
        double lowMhz = options.CentreMhz + (options.LowEdgeHz / 1e6);
        double highMhz = options.CentreMhz + (options.HighEdgeHz / 1e6);

        Console.WriteLine("flex-iq-noise — Gaussian white noise over the FlexRadio Waveform API");
        Console.WriteLine();
        Console.WriteLine($"  centre    {options.CentreMhz:F6} MHz   ({options.Antenna}, underlying_mode={options.UnderlyingMode})");

        if (options.Placed)
        {
            (double placedLow, double placedHigh) = options.PlacedBandMhz;
            Console.WriteLine($"  band      {placedLow:F6} – {placedHigh:F6} MHz   ({options.BandwidthHz:F0} Hz wide, "
                + $"{(options.BandReference == IqBandReference.LowerEdge ? "lower-edge" : "centre")}-referenced, "
                + "placed by the library)");
        }

        if (options.IsExplore)
        {
            int start = options.ExploreStartHz!.Value;
            string where = options.Placed
                ? $"{options.PlacedBandMhz.LowMhz + ((start - options.PlacedBasebandRange.Low) / 1e6):F6} MHz"
                : $"{options.CentreMhz + (start / 1e6):F6} MHz";
            Console.WriteLine($"  explore   starting at {start:+#;-#;0} Hz baseband  =  {where}"
                + "   (retuned live; the dial never moves)");
        }
        else if (options.Placed)
        {
            // The band line above already says where it goes.
        }
        else if (options.IsTone)
        {
            var placed = new List<string>();
            foreach (double offset in options.ToneOffsetsHz)
            {
                placed.Add($"{options.CentreMhz + (offset / 1e6):F6} MHz ({offset:+#;-#;0} Hz)");
            }

            Console.WriteLine($"  tones     {string.Join(", ", placed)}");
        }
        else
        {
            Console.WriteLine($"  band      {lowMhz:F6} – {highMhz:F6} MHz   ({options.BandwidthHz:F0} Hz wide"
                + (options.OffsetHz == 0 ? ")" : $", offset {options.OffsetHz:+#;-#;0} Hz)"));
        }
        if (!options.IsExplore)
        {
            Console.WriteLine($"  duration  {options.Seconds:F1} s   ({(long)(options.Seconds * Options.SampleRate):N0} complex samples at {Options.SampleRate} Hz)");
        }
        Console.WriteLine($"  power     {options.PowerWatts:F0} W set, drive {options.DriveBackoffDb:F1} dBFS "
            + $"→ ≈{options.AveragePowerWatts:F2} W average");
        // The filter that actually caps occupied bandwidth is the radio's transmit filter, not the
        // waveform's tx_filter (which is accepted and ignored on this firmware).
        (int txLo, int txHi) = options.TransmitFilter ?? (0, options.RequiredTxFilterHighHz);
        WarnAboveDc(options);

        Console.WriteLine($"  tx filter {txLo:N0} … {txHi:N0} Hz from the carrier"
            + (txHi > Options.MaxTransmitFilterHighHz
                ? $"   ← clamps to {Options.MaxTransmitFilterHighHz:N0}, so the band will be cut"
                : ""));
        Console.WriteLine();
    }

    /// <summary>
    /// Says so, loudly, when part of the request cannot reach the air — because only content below DC
    /// is transmitted, in any mode.
    /// </summary>
    /// <remarks>
    /// Without this the plan line states a band the radio was never going to produce, the burst runs
    /// its full length and the run exits 0. Printing a frequency span the tool knows is unachievable
    /// is the same silent-wrongness this rig exists to catch in the radio.
    /// </remarks>
    private static void WarnAboveDc(Options options)
    {
        if (options.IsExplore || options.Placed || options.UntransmittableHighHz <= 0)
        {
            return;
        }

        if (options.IsEntirelyAboveDc)
        {
            Console.WriteLine("  ← NOTHING WILL BE TRANSMITTED: all of this sits above DC, and only "
                + "content below DC reaches the air.");
            Console.WriteLine("    Expect carrier leakage and no signal. Drop --offset to place the band "
                + "below the carrier, where it transmits.");
            return;
        }

        double lost = options.UntransmittableHighHz;
        double kept = -options.LowEdgeHz;
        Console.WriteLine($"  ← ONLY {kept:F0} Hz OF THIS WILL BE TRANSMITTED: the {lost:F0} Hz above DC "
            + "does not reach the air in any mode.");
        Console.WriteLine($"    On air: {options.CentreMhz - (kept / 1e6):F6} – {options.CentreMhz:F6} MHz. "
            + "Drop --offset to transmit the whole width.");
    }

    private static int RunDryRun(Options options)
    {
        Console.WriteLine("dry run — generating the burst, not keying the radio");
        Console.WriteLine();

        var generator = new BurstGenerator(options, BlockPairs);
        long totalPairs = TotalPairs(options);
        var block = new float[BlockPairs * 2];

        for (long done = 0; done < totalPairs;)
        {
            int take = (int)Math.Min(BlockPairs, totalPairs - done);
            Span<float> span = block.AsSpan(0, take * 2);
            generator.Fill(span);
            done += take;
        }

        generator.Finish();
        ReportSignal(options, generator);
        return ReportSpectrumOf(options, generator.AnalysisSamples, "spectrum", options.CsvPath) ? 0 : 1;
    }

    private static readonly CancellationTokenSource _cancellation = new();

    /// <summary>Registered once, so a sweep of many transmit cycles still has exactly one handler.</summary>
    private static void InstallInterruptHandler()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;                                // we unkey ourselves rather than die keyed
            _aborted = true;
            _cancellation.Cancel();
            Console.WriteLine();
            Console.WriteLine("interrupted — unkeying");
        };
    }

    /// <summary>
    /// Walks a list of <c>underlying_mode</c> values, transmitting the same probe under each, so one
    /// run re-derives the whole mode table. Each mode needs its own waveform registration,
    /// so every segment is a full connect → register → key → tear down cycle.
    /// </summary>
    private static async Task<int> RunSweepAsync(Options options)
    {
        Console.WriteLine("sweep — the same probe under each underlying_mode, in order:");
        Console.WriteLine();
        for (int i = 0; i < options.SweepModes.Length; i++)
        {
            Console.WriteLine($"    {i + 1}. underlying_mode={options.SweepModes[i]}"
                + $"   {options.Seconds:F0} s, then a {SweepGapSeconds:F0} s gap");
        }

        Console.WriteLine();
        Console.WriteLine("One tone is sent per segment, at -3 kHz baseband. Note which side of the");
        Console.WriteLine("carrier it lands on — that alone identifies the mode:");
        Console.WriteLine("    below the carrier  -> upright  (usable for a modulated signal)");
        Console.WriteLine("    above the carrier  -> mirrored (the spectrum is inverted)");
        Console.WriteLine("    nothing            -> that mode does not transmit our IQ");
        Console.WriteLine();

        int failures = 0;
        for (int i = 0; i < options.SweepModes.Length && !_aborted; i++)
        {
            string mode = options.SweepModes[i];
            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"segment {i + 1}/{options.SweepModes.Length}: underlying_mode={mode}");
            Console.WriteLine(new string('─', 72));

            try
            {
                if (await RunTransmitAsync(options with { UnderlyingMode = mode }).ConfigureAwait(false) != 0)
                {
                    failures++;
                }
            }
            catch (FlexProtocolException ex)
            {
                // A mode the firmware will not register is itself a result — record it and continue.
                Console.Error.WriteLine($"  underlying_mode={mode} rejected: {ex.Message}");
                failures++;
            }

            if (i + 1 < options.SweepModes.Length && !_aborted)
            {
                Console.WriteLine($"  … {SweepGapSeconds:F0} s gap …");
                await Task.Delay(TimeSpan.FromSeconds(SweepGapSeconds), CancellationToken.None).ConfigureAwait(false);
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunTransmitAsync(Options options)
    {
        CancellationTokenSource cancellation = _cancellation;
        _keyed = false;

        Console.WriteLine($"connecting to {options.Radio} …");
        (FlexClient client, MockFlexRadio? mock) =
            await ConnectAsync(options, cancellation.Token).ConfigureAwait(false);

        FlexWaveform? waveform = null;
        try
        {
            (int txLo, int txHi) = options.TransmitFilter ?? (0, options.RequiredTxFilterHighHz);
            var waveformOptions = new FlexWaveformOptions
            {
                Name = WaveformName,
                Mode = WaveformMode,
                UnderlyingMode = options.UnderlyingMode,
                Antenna = options.Antenna,
                TxFilterLowHz = options.TxFilterLowHz,
                TxFilterHighHz = options.TxFilterHighHz,
                TransmitFilterLowHz = txLo,
                TransmitFilterHighHz = txHi,

                // Exactly one of these: place a declared band, or tune the slice and place the IQ
                // by hand. Which one is in use is now visible here rather than implied.
                Band = options.Placed
                    ? new IqBand(options.CentreMhz, (int)Math.Round(options.BandwidthHz), options.BandReference)
                    : null,
                SliceFrequencyMhz = options.Placed ? null : options.CentreMhz,
                RfPower = (int)Math.Round(options.PowerWatts),
            };

            var interlockStates = new List<string>();
            client.StatusUpdated += update =>
            {
                if (update.Object.StartsWith("interlock", StringComparison.OrdinalIgnoreCase)
                    && update.Updated.TryGetValue("state", out string? state))
                {
                    lock (interlockStates)
                    {
                        if (interlockStates.Count == 0 || interlockStates[^1] != state)
                        {
                            interlockStates.Add(state);
                        }
                    }
                }
            };

            waveform = await FlexWaveform.SetUpHeadlessAsync(client, waveformOptions, cancellation.Token)
                .ConfigureAwait(false);

            Console.WriteLine($"waveform '{WaveformName}' registered, slice {waveform.SliceIndex} on "
                + $"{options.CentreMhz:F6} MHz in mode {WaveformMode}");
            if (waveform.TuneWarning is string warning)
            {
                Console.WriteLine($"  warning: {warning}");
            }

            if (waveform.OccupiedBand is (double bandLow, double bandHigh))
            {
                Console.WriteLine($"  placed     band {bandLow:F6} – {bandHigh:F6} MHz"
                    + $"   slice {waveform.SliceFrequencyMhz:F6} MHz"
                    + $"   shift {waveform.BasebandShiftHz:+#;-#;0} Hz");
            }

            if (waveform.TransmitFilter is (int gotLow, int gotHigh))
            {
                Console.WriteLine($"  tx filter  {gotLow}–{gotHigh} Hz from the carrier (applied by the library)");
            }

            if (waveform.TransmitFilterWarning is string filterWarning)
            {
                Console.WriteLine($"  ← {filterWarning}");
            }

            ReportSliceReadback(client, waveform.SliceIndex, options);
            await ApplyFilterProbesAsync(client, waveform, options, cancellation.Token).ConfigureAwait(false);

            if (options.NoTx)
            {
                Console.WriteLine();
                Console.WriteLine("  --no-tx: set up and probed, nothing keyed");
                return 0;
            }

            using FlexWaveformIqOutput iq = waveform.CreateIqOutput(bufferSeconds: 2.0);
            FlexPtt ptt = waveform.CreatePtt(confirmInterlock: true);

            BurstResult burst = BurstResult.NotKeyed;
            var generator = new BurstGenerator(options, BlockPairs);
            try
            {
                burst = options.IsExplore
                    ? await ExploreAsync(options, iq, ptt, waveform.OccupiedBand, cancellation.Token).ConfigureAwait(false)
                    : await StreamBurstAsync(options, iq, ptt, generator, cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                if (_keyed)
                {
                    ptt.Unkey();
                }

                generator.Finish();
            }

            Console.WriteLine();
            lock (interlockStates)
            {
                Console.WriteLine($"  interlock  {(interlockStates.Count > 0 ? string.Join(" → ", interlockStates) : "(no transitions seen)")}");
            }

            Console.WriteLine($"  reflected  {burst.PacketsReflected:N0} TX buffers"
                + $" ({burst.PacketsReflected * (double)PacketPairs / Options.SampleRate:F2} s on air)");
            Console.WriteLine($"  starved    {burst.Starved:N0} complex samples"
                + (burst.Starved == 0 ? "" : "  ← GAPS IN THE BURST, spectrum not trustworthy"));

            if (options.IsExplore)
            {
                // Nothing to analyse: the signal deliberately changed frequency throughout.
                return burst.Starved == 0 && !_aborted ? 0 : 1;
            }

            ReportSignal(options, generator);
            bool clean = ReportSpectrumOf(
                options, generator.AnalysisSamples, "spectrum (as generated)", options.CsvPath);

            if (mock is not null)
            {
                // Closes the loop offline: this is what came back off the wire, so it exercises the
                // ring, the big-endian float32 packetize and the VITA transport, not just the maths.
                //
                // Under band placement the library frequency-shifts on the way out, so the captured
                // samples sit where IT put them, not where the caller wrote them. Analyse against the
                // shifted band or every placed run reports a false alarm.
                Options asSent = options with { OffsetHz = options.OffsetHz + waveform.BasebandShiftHz };

                bool received = ReportSpectrumOf(
                    asSent, [.. mock.CapturedWaveformIq], "spectrum (as received by the radio)", null);
                clean = clean && received;
            }

            bool healthy = clean && burst.Starved == 0 && !_aborted;
            return healthy ? 0 : 1;
        }
        finally
        {
            if (waveform is not null)
            {
                await waveform.DisposeAsync().ConfigureAwait(false);   // also disposes the client
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

    private const int ExploreBlockPairs = 256;              // ~11 ms, a whole number of TX buffers
    private const int ExploreQueuePairs = 1200;             // 50 ms of slack, so a keypress is heard

    /// <summary>
    /// Keys once and transmits a single tone the keyboard retunes live, for walking the radio's
    /// passband. Returns when the operator quits.
    /// </summary>
    /// <remarks>
    /// <para>The tone moves because the IQ is regenerated at a new offset — the slice frequency is
    /// never touched. Retuning the dial instead would drag the passband along with the tone and
    /// measure nothing.</para>
    /// <para>The transmit queue is held deliberately shallow (~50 ms). Letting
    /// <see cref="FlexWaveformIqOutput.Write"/> block on a full ring would buffer up to half a
    /// second of already-generated tone, so every keypress would take that long to reach the air and
    /// the control would feel broken.</para>
    /// </remarks>
    private static async Task<BurstResult> ExploreAsync(
        Options options, FlexWaveformIqOutput iq, FlexPtt ptt,
        (double LowMhz, double HighMhz)? placedBand, CancellationToken cancellation)
    {
        if (Console.IsInputRedirected)
        {
            throw new ArgumentException("--explore needs a terminal to read arrow keys from");
        }

        var source = new TunableToneSource(options.Rms, options.ExploreStartHz!.Value);
        (double rangeLow, double rangeHigh) = options.Placed
            ? options.PlacedBasebandRange
            : (-options.NyquistHz, options.NyquistHz);

        if (options.Placed)
        {
            // Outside the declared band the placement contract says nothing and the library's filter
            // cuts it, so keep the sweep inside what was actually asked for.
            source.OffsetHz = (int)Math.Clamp(source.OffsetHz, rangeLow, rangeHigh);
        }
        var block = new float[ExploreBlockPairs * 2];

        // Queue a little before keying: the sink only drains while the interlock says we are
        // transmitting, so this is what makes the tone present from the first moment of RF.
        for (int i = 0; i < 5; i++)
        {
            source.Fill(block);
            iq.Write(block);
        }

        Console.WriteLine();
        Console.WriteLine("  up/down ±100 Hz   left/right ±10 Hz   PgUp/PgDn ±1000 Hz   0 recentre   q quit");
        Console.WriteLine();
        _keyed = true;
        ptt.Key();

        long written = ExploreBlockPairs * 5L;
        var stop = new CancellationTokenSource();

        var producer = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested && !_aborted)
                {
                    // Stay just ahead of the radio rather than filling the ring, so a retune is
                    // audible within ~50 ms instead of a buffer-depth later.
                    long queued = written - (iq.PacketsReflected * PacketPairs);
                    if (queued > ExploreQueuePairs)
                    {
                        await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }

                    source.Fill(block);
                    iq.Write(block);
                    written += ExploreBlockPairs;
                }
            },
            CancellationToken.None);

        int lastShown = int.MinValue;
        while (!_aborted && !stop.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                int offset = source.OffsetHz;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: offset += 100; break;
                    case ConsoleKey.DownArrow: offset -= 100; break;
                    case ConsoleKey.RightArrow: offset += 10; break;
                    case ConsoleKey.LeftArrow: offset -= 10; break;
                    case ConsoleKey.PageUp: offset += 1000; break;
                    case ConsoleKey.PageDown: offset -= 1000; break;
                    case ConsoleKey.D0 or ConsoleKey.NumPad0: offset = 0; break;
                    case ConsoleKey.Q or ConsoleKey.Escape: stop.Cancel(); continue;
                    default: continue;
                }

                // Beyond Nyquist the tone would alias back in-band and read as a false passband;
                // beyond the declared band, placement makes no promise.
                source.OffsetHz = (int)Math.Clamp(offset, rangeLow, rangeHigh);
            }
            else
            {
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }

            if (source.OffsetHz != lastShown)
            {
                lastShown = source.OffsetHz;
                // Under placement the slice has moved, so the tone's RF follows the band the library
                // reported — not the frequency that was requested.
                // Without placement the mode decides the direction: a mirroring mode surfaces a
                // baseband component at −f above the carrier, so RF runs the other way.
                bool mirrors = options.UnderlyingMode is "IQ" or "USB" or "DIGU";
                double rfMhz = placedBand is (double low, double _)
                    ? low + ((lastShown - rangeLow) / 1e6)
                    : options.CentreMhz + ((mirrors ? -lastShown : lastShown) / 1e6);

                // Kept under 80 columns so it overwrites cleanly instead of wrapping.
                Console.Write($"\r  tone {lastShown,+6} Hz  {rfMhz,10:F6} MHz  "
                    + $"{PassbandPrediction(options, lastShown, placedBand is not null),-34}");
            }
        }

        // Snapshot starvation while still transmitting: an underrun here is a real gap in the tone.
        // Past this point the ring is deliberately being emptied, so the radio's next pull starves
        // by design and would otherwise be reported as a fault.
        long starved = iq.SamplesStarved;

        await stop.CancelAsync().ConfigureAwait(false);
        await producer.ConfigureAwait(false);
        Console.WriteLine();

        iq.Drain(TimeSpan.FromSeconds(2));
        return new BurstResult(true, starved, iq.PacketsReflected);
    }

    /// <summary>
    /// What the radio is expected to do with a tone at this offset, from the measured behaviour of
    /// the waveform path. Advisory — the point of exploring is to check it.
    /// </summary>
    private static string PassbandPrediction(Options options, int offsetHz, bool placed)
    {
        // Under placement the library owns the sideband and the filter, so the contract is simply
        // that everything inside the declared band reaches the air at the stated frequency.
        if (placed)
        {
            return "expect: PASS (placed)";
        }

        string mode = options.UnderlyingMode;
        bool mirrors = mode is "IQ" or "USB" or "DIGU";
        bool upright = mode is "RAW" or "LSB" or "DIGL";
        int limit = Math.Min(options.RequiredTxFilterHighHz, Options.MaxTransmitFilterHighHz);

        if (Math.Abs(offsetHz) > limit)
        {
            return $"expect: blocked, past {limit} Hz filter";
        }

        // Only negative baseband reaches the air, in every mode.
        if (offsetHz > 0 && (upright || mirrors))
        {
            return "expect: blocked, +ve baseband";
        }

        if (offsetHz == 0)
        {
            return "expect: at the carrier";
        }

        // The mode decides which side of the carrier it surfaces on.
        return mirrors ? $"expect: PASS, at +{-offsetHz} Hz (mirrored)" : "expect: PASS";
    }

    /// <summary>How the burst went, sampled at the end of the drain and <i>before</i> the unkey. The
    /// library always flushes one zero-filled packet after UNKEY_REQUESTED, so a counter read after
    /// teardown always shows 128 starved samples that never went on air.</summary>
    private sealed record BurstResult(bool Keyed, long Starved, long PacketsReflected)
    {
        public static readonly BurstResult NotKeyed = new(false, 0, 0);
    }

    /// <summary>
    /// Queues a second of noise <i>before</i> keying — the sink only drains while the interlock says
    /// we are transmitting, so this is what makes the burst start clean instead of starved — then
    /// keys and streams the rest, paced by the radio's own pull rate.
    /// </summary>
    private static async Task<BurstResult> StreamBurstAsync(
        Options options, FlexWaveformIqOutput iq, FlexPtt ptt, BurstGenerator generator,
        CancellationToken cancellation)
    {
        long totalPairs = TotalPairs(options);
        long prefillPairs = Math.Min(totalPairs, Options.SampleRate);
        var block = new float[BlockPairs * 2];
        long written = 0;

        while (written < prefillPairs && !_aborted)
        {
            int take = (int)Math.Min(BlockPairs, prefillPairs - written);
            Span<float> span = block.AsSpan(0, take * 2);
            generator.Fill(span);
            iq.Write(span);
            written += take;
        }

        if (_aborted)
        {
            return BurstResult.NotKeyed;
        }

        Console.WriteLine($"keying — {options.Seconds:F1} s of "
            + $"{(options.IsTone ? "tones" : "noise")} into {options.Antenna}");
        _keyed = true;
        ptt.Key();

        // The producer runs off-thread because Write() blocks on the radio's drain rate; the main
        // thread stays free to show progress and to give up if the radio never actually pulls.
        long producerWritten = written;
        var producer = Task.Run(
            () =>
            {
                var producerBlock = new float[BlockPairs * 2];
                while (Interlocked.Read(ref producerWritten) < totalPairs && !_aborted)
                {
                    int take = (int)Math.Min(BlockPairs, totalPairs - Interlocked.Read(ref producerWritten));
                    Span<float> span = producerBlock.AsSpan(0, take * 2);
                    generator.Fill(span);
                    iq.Write(span);
                    Interlocked.Add(ref producerWritten, take);
                }
            },
            CancellationToken.None);

        long lastPackets = -1;
        int stalledTicks = 0;
        while (!producer.IsCompleted)
        {
            await Task.WhenAny(producer, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);

            long packets = iq.PacketsReflected;
            long queued = Interlocked.Read(ref producerWritten);
            double onAir = packets * (double)PacketPairs / Options.SampleRate;
            string progress = $"  transmitting {onAir,6:F1} / {options.Seconds:F1} s   "
                + $"queued {queued * 100.0 / totalPairs,5:F1} %   starved {iq.SamplesStarved}";

            // Redirected output gets one line per update rather than a carriage-returned status line,
            // so a tee'd bench log stays readable.
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(progress);
            }
            else
            {
                Console.Write($"\r{progress}   ");
            }

            // If the radio is not pulling, Write() is blocked forever — don't hang the bench.
            stalledTicks = packets == lastPackets ? stalledTicks + 1 : 0;
            lastPackets = packets;
            if (stalledTicks >= 20)
            {
                Console.WriteLine();
                Console.Error.WriteLine("error: the radio stopped requesting transmit buffers (10 s) — aborting");
                _aborted = true;
                iq.Dispose();                                   // unblocks the producer
                break;
            }

            if (cancellation.IsCancellationRequested)
            {
                _aborted = true;
                iq.Dispose();
                break;
            }
        }

        await producer.ConfigureAwait(false);
        Console.WriteLine();

        if (!_aborted && !iq.Drain(TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine("warning: the transmit buffer did not drain within 10 s");
        }

        // Read the counters here — after the drain, before the caller unkeys — so the flush packet
        // is not counted as a gap in the burst.
        return new BurstResult(true, iq.SamplesStarved, iq.PacketsReflected);
    }

    /// <summary>
    /// Re-applies the transmit filter with its error code shown, then any slice filter and raw
    /// probe commands, before keying.
    /// </summary>
    /// <remarks>
    /// The library sets the waveform's <c>tx_filter</c> during setup with an expect-OK send, so a
    /// command that returns err=0 and is then ignored looks identical to one that worked. Measured
    /// on the 6500, the transmit passband stays about 3 kHz however wide the filter is set — so the
    /// codes are worth seeing, and the slice-level <c>filt</c> is worth a separate try.
    /// </remarks>
    private static async Task ApplyFilterProbesAsync(
        FlexClient client, FlexWaveform waveform, Options options, CancellationToken cancellation)
    {
        async Task SendAsync(string command)
        {
            try
            {
                FlexResult result = await client.SendCommandAsync(command, cancellation).ConfigureAwait(false);
                Console.WriteLine($"  probe      {command}   → err=0x{result.Error:X8}"
                    + (result.Message.Length > 0 ? $" \"{result.Message}\"" : "")
                    + (result.IsOk ? "" : "   ← REJECTED"));
            }
            catch (FlexProtocolException ex)
            {
                Console.WriteLine($"  probe      {command}   → faulted: {ex.Message}");
            }
        }

        // Subscribe to the transmitter object so its filter is readable — the ~3 kHz transmit
        // passband lives here, not on the slice (whose filter_lo/filter_hi is the RX filter).
        await SendAsync("sub tx all").ConfigureAwait(false);

        // Re-send the waveform filter so its result code is visible rather than swallowed by setup.
        await SendAsync($"waveform set {waveform.WaveformName} tx_filter low_cut={options.TxFilterLowHz}")
            .ConfigureAwait(false);
        await SendAsync($"waveform set {waveform.WaveformName} tx_filter high_cut={options.TxFilterHighHz}")
            .ConfigureAwait(false);

        if (options.SliceFilter is (int lo, int hi))
        {
            await SendAsync($"filt {waveform.SliceIndex} {lo} {hi}").ConfigureAwait(false);
        }

        foreach (string command in options.PostCommands)
        {
            await SendAsync(command).ConfigureAwait(false);
        }

        // Status arrives asynchronously, so re-reading immediately would report "unchanged" for a
        // command that did in fact take. Give the radio a moment before believing the readback.
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);

        ReportTransmitReadback(client, options);
        ReportSliceReadback(client, waveform.SliceIndex, options);
    }

    /// <summary>Prints the transmitter's filter and drive settings — where the SSB transmit passband
    /// actually lives.</summary>
    private static void ReportTransmitReadback(FlexClient client, Options options)
    {
        if (!client.TryFindObject("transmit", _ => true, out string objectName)
            || !client.TryGetObject(objectName, out IReadOnlyDictionary<string, string> transmit))
        {
            Console.WriteLine("  transmit   (the radio reported no transmit status)");
            return;
        }

        // The transmit passband is reported as lo/hi (it is SET with filter_low=/filter_high=).
        string[] interesting =
            ["lo", "hi", "tx_filter_changes_allowed", "tx_slice_mode", "rfpower", "max_power_level", "tune"];
        var parts = new List<string>();
        foreach (string key in interesting)
        {
            if (transmit.TryGetValue(key, out string? value))
            {
                parts.Add($"{key}={value}");
            }
        }

        Console.WriteLine($"  transmit   {(parts.Count > 0 ? string.Join(" ", parts) : "(no recognised keys)")}");

        if (options.Verbose)
        {
            foreach (KeyValuePair<string, string> pair in transmit)
            {
                Console.WriteLine($"             {pair.Key}={pair.Value}");
            }
        }
    }

    /// <summary>
    /// Prints what the radio says the slice ended up as. A command returning err=0 does not mean it
    /// was applied — the band-persistence bug (docs/flex-integration.md §8) is the standing proof —
    /// so the mode the slice actually reports is worth seeing before believing any measurement.
    /// </summary>
    private static void ReportSliceReadback(FlexClient client, string sliceIndex, Options options)
    {
        if (!client.TryGetObject($"slice {sliceIndex}", out IReadOnlyDictionary<string, string> slice))
        {
            Console.WriteLine("  readback   (the radio reported no slice status)");
            return;
        }

        string[] interesting = ["mode", "RF_frequency", "tx", "txant", "rxant", "wide", "filter_lo", "filter_hi"];
        var parts = new List<string>();
        foreach (string key in interesting)
        {
            if (slice.TryGetValue(key, out string? value))
            {
                parts.Add($"{key}={value}");
            }
        }

        Console.WriteLine($"  readback   {(parts.Count > 0 ? string.Join(" ", parts) : "(no recognised keys)")}");

        if (slice.TryGetValue("mode", out string? mode)
            && !mode.Equals(WaveformMode, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  ← slice reports mode={mode}, not {WaveformMode}: the waveform is NOT active, "
                + $"so this is ordinary {mode} modulation of the I channel, not complex IQ");
        }

        if (options.Verbose)
        {
            foreach (KeyValuePair<string, string> pair in slice)
            {
                Console.WriteLine($"             {pair.Key}={pair.Value}");
            }
        }
    }

    private static async Task<(FlexClient Client, MockFlexRadio? Mock)> ConnectAsync(
        Options options, CancellationToken cancellation)
    {
        if (options.Radio.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
            mock.Start();
            FlexClient mockClient = await FlexClient.ConnectAsync(
                "127.0.0.1", mock.TcpPort, mock.UdpPort, cancellation).ConfigureAwait(false);
            return (mockClient, mock);
        }

        bool looksLikeHost = options.Radio.Contains('.', StringComparison.Ordinal)
            && !options.Radio.Contains('=', StringComparison.Ordinal);

        FlexClient client = looksLikeHost
            ? await FlexClient.ConnectAsync(options.Radio, cancellation: cancellation).ConfigureAwait(false)
            : await FlexClient.DiscoverAndConnectAsync(
                options.Radio is "discover" ? null : options.Radio,
                TimeSpan.FromSeconds(5),
                cancellation).ConfigureAwait(false);
        return (client, null);
    }

    /// <summary>
    /// The burst length in complex samples, rounded up to a whole number of transmit buffers. The
    /// radio pulls a fixed 128 complex samples at a time and zero-pads any shortfall, so a duration
    /// that is not a whole number of buffers (5 s is 937.5 of them) would end with a part-filled
    /// packet — counted as a starve, and a real if tiny discontinuity. Rounding up costs at most
    /// 5.3 ms of airtime and makes a clean burst report exactly zero starved samples.
    /// </summary>
    private static long TotalPairs(Options options)
    {
        long pairs = (long)Math.Round(options.Seconds * Options.SampleRate);
        return ((pairs + PacketPairs - 1) / PacketPairs) * PacketPairs;
    }

    private static void ReportSignal(Options options, BurstGenerator generator)
    {
        ISignalSource source = generator.Source;
        Console.WriteLine();
        Console.WriteLine("signal");
        Console.WriteLine($"  shaping    {source.Description}");
        Console.WriteLine($"  rms        {source.MeasuredRms:F4} per component (asked {options.Rms:F4})");
        Console.WriteLine($"  peak       {source.PeakSample:F4}  →  crest {20 * Math.Log10(source.PeakSample / Math.Max(source.MeasuredRms, 1e-9)):F1} dB");
        Console.WriteLine($"  clipped    {source.ClippedSamples:N0}"
            + (source.ClippedSamples == 0 ? "" : "  ← CLIPPING SPLATTERS OUTSIDE THE BAND, lower --rms"));

        if (generator.WavPath is string path)
        {
            Console.WriteLine($"  wav        {path}");
        }
    }

    /// <summary>Audits noise for flatness in band and how far down outside it. Returns false if it is
    /// not clean enough for a conclusion about the radio to be drawn from it.</summary>
    private static bool ReportSpectrumOf(Options options, float[]? samples, string heading, string? csvPath)
    {
        Console.WriteLine();
        if (samples is null || samples.Length / 2 < AnalysisFftSize)
        {
            Console.WriteLine($"{heading}   (too few samples to analyse)");
            return true;
        }

        double[] spectrum = Spectrum.Estimate(samples, AnalysisFftSize);

        if (options.IsTone)
        {
            return ReportTones(options, spectrum, heading, csvPath);
        }

        double half = options.BandwidthHz / 2;

        // Measure across the middle 80 % of the band, clear of the filter's own skirts.
        double? inBand = Spectrum.MeanDbOver(
            spectrum, Options.SampleRate, options.OffsetHz - (half * 0.8), options.OffsetHz + (half * 0.8));
        double? ripple = Spectrum.StdDevDbOver(
            spectrum, Options.SampleRate, options.OffsetHz - (half * 0.8), options.OffsetHz + (half * 0.8));

        Console.WriteLine($"{heading}   (Welch, {AnalysisFftSize}-point, {Options.SampleRate / (double)AnalysisFftSize:F1} Hz bins)");

        if (inBand is not double reference)
        {
            Console.WriteLine("  the requested band covers no analysis bins — widen --bw");
            return false;
        }

        Console.WriteLine($"  in band    flat to ±{ripple ?? 0:F2} dB (1σ across the middle 80 %)");

        foreach (double drop in (double[])[3, 20, 60])
        {
            (double lowHz, double highHz) = Spectrum.WidthAtDb(
                spectrum, Options.SampleRate, options.OffsetHz, reference, drop);
            Console.WriteLine($"  −{drop,-2:F0} dB     {highHz - lowHz,7:F0} Hz wide   ({lowHz:+#;-#;0} … {highHz:+#;-#;0} Hz from centre)");
        }

        (double obwLow, double obwHigh) = Spectrum.OccupiedBandwidth(spectrum, Options.SampleRate, 0.99);
        Console.WriteLine($"  99 % OBW   {obwHigh - obwLow,7:F0} Hz wide   ({obwLow:+#;-#;0} … {obwHigh:+#;-#;0} Hz from centre)");

        // What is left beyond twice the half-bandwidth is the rig's own floor. It has to sit far
        // below anything the radio might do, or a measured skirt could be ours rather than the radio's.
        bool clean = true;
        double farEdge = half * 2;
        double? farHigh = farEdge < options.NyquistHz
            ? Spectrum.MeanDbOver(spectrum, Options.SampleRate, options.OffsetHz + farEdge, options.NyquistHz)
            : null;
        double? farLow = -farEdge > -options.NyquistHz
            ? Spectrum.MeanDbOver(spectrum, Options.SampleRate, -options.NyquistHz, options.OffsetHz - farEdge)
            : null;

        if (farHigh is not null || farLow is not null)
        {
            double floorDb = Math.Max(farHigh ?? double.NegativeInfinity, farLow ?? double.NegativeInfinity) - reference;
            Console.WriteLine($"  floor      {floorDb,7:F0} dB beyond ±{farEdge:F0} Hz");
            clean = floorDb <= -60;
            if (!clean)
            {
                Console.WriteLine("  ← the rig's own out-of-band energy is high; do not attribute it to the radio");
            }
        }

        if (csvPath is not null)
        {
            WriteCsv(csvPath, spectrum, options, reference);
            Console.WriteLine($"  csv        {csvPath}");
        }

        return clean;
    }

    /// <summary>
    /// For each requested tone, the level found at that offset and at its mirror. The ratio between
    /// them is the whole diagnostic: high means a properly complex path, ~0 dB means the path is real
    /// (Q ignored), and a mirror stronger than the wanted tone means the spectrum is inverted.
    /// </summary>
    private static bool ReportTones(Options options, double[] spectrum, string heading, string? csvPath)
    {
        // Take the strongest bin within a couple of bins of the nominal offset, so a small frequency
        // error does not read as a missing tone.
        double binHz = Options.SampleRate / (double)AnalysisFftSize;
        double window = binHz * 2;

        double PeakNear(double offsetHz)
        {
            double best = double.NegativeInfinity;
            for (int k = 0; k < spectrum.Length; k++)
            {
                double f = Spectrum.BinFrequencyHz(k, spectrum.Length, Options.SampleRate);
                if (Math.Abs(f - offsetHz) <= window)
                {
                    best = Math.Max(best, spectrum[k]);
                }
            }

            return best;
        }

        double strongest = double.NegativeInfinity;
        foreach (double offset in options.ToneOffsetsHz)
        {
            strongest = Math.Max(strongest, PeakNear(offset));
        }

        Console.WriteLine($"{heading}   (Welch, {AnalysisFftSize}-point, {binHz:F1} Hz bins)");
        Console.WriteLine("  requested     wanted     mirror   image rejection");
        if (options.ToneOffsetsHz.Length > 1)
        {
            Console.WriteLine("  (a symmetric probe mirrors onto itself, so the rejection column is 0 dB by construction)");
        }

        bool complex = true;
        foreach (double offset in options.ToneOffsetsHz)
        {
            double wanted = PeakNear(offset) - strongest;
            double mirror = PeakNear(-offset) - strongest;
            double rejection = wanted - mirror;
            Console.WriteLine(
                $"  {offset,+7:F0} Hz   {wanted,7:F1} dB {mirror,7:F1} dB   {rejection,7:F1} dB");

            // This measures the GENERATED signal, so it audits the rig rather than the radio: a
            // clean complex tone should have essentially nothing in its image. What the radio does
            // with it is a separate question, answered by where the tone lands on a receiver.
            //
            // Only meaningful when the mirror was not itself requested: in a symmetric probe like
            // 3k,-3k each tone IS the other's mirror, so the ratio is 0 dB by construction and says
            // nothing at all.
            bool mirrorAlsoRequested = Array.Exists(
                options.ToneOffsetsHz, other => Math.Abs(other + offset) < window);

            if (offset != 0 && !mirrorAlsoRequested && rejection < 10)
            {
                complex = false;
            }
        }

        if (!complex)
        {
            Console.WriteLine("  ← mirror image is comparable to the wanted tone: this path is NOT complex");
        }

        if (csvPath is not null)
        {
            WriteCsv(csvPath, spectrum, options, strongest);
            Console.WriteLine($"  csv        {csvPath}");
        }

        return complex;
    }

    private static void WriteCsv(string path, double[] spectrum, Options options, double reference)
    {
        var text = new StringBuilder("offset_hz,rf_hz,power_db,power_db_rel\n");
        double centreHz = options.CentreMhz * 1e6;
        for (int k = 0; k < spectrum.Length; k++)
        {
            double offset = Spectrum.BinFrequencyHz(k, spectrum.Length, Options.SampleRate);
            text.Append(CultureInfo.InvariantCulture, $"{offset:F2},{centreHz + offset:F2},{spectrum[k]:F3},{spectrum[k] - reference:F3}\n");
        }

        File.WriteAllText(path, text.ToString());
    }
}
