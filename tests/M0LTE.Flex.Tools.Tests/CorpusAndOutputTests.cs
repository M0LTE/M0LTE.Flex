using M0LTE.Flex.Tools.IqGen;
using GenFormat = M0LTE.Flex.Tools.IqGen.IqFormat;
using TxMeta = M0LTE.Flex.Tools.IqTx.SigMfMeta;

namespace M0LTE.Flex.Tools.Tests;

/// <summary>
/// The corpus itself, and the file outputs either tool produces. The corpus is a deliverable other
/// people read expectations from, so its contents and its README have to stay in step.
/// </summary>
public sealed class CorpusAndOutputTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_corpus_writes_every_entry_and_a_readme_describing_it()
    {
        string dir = TempDir();
        Corpus.Write(dir, GenFormat.Cf32).Should().Be(0);

        string[] expected =
        [
            "tone-minus3k", "tone-plus3k", "twotone-2k-5k", "noise-3k",
            "noise-10k", "chirp-10k", "staircase-10k", "qpsk-2k4",
        ];

        string readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        foreach (string name in expected)
        {
            string path = Path.Combine(dir, $"{name}.cf32");
            File.Exists(path).Should().BeTrue($"{name} is part of the corpus");
            new FileInfo(path).Length.Should().BeGreaterThan(0);

            // Every file must be described, or someone runs it with no idea what to expect.
            readme.Should().Contain(name);
        }
    }

    [Fact]
    public void The_falsification_entry_is_the_only_one_above_dc()
    {
        // tone-plus3k exists to prove the half that should never transmit stays silent. If it ever
        // drifted below DC it would quietly become a duplicate of tone-minus3k and stop testing
        // anything, while still appearing to pass.
        string dir = TempDir();
        Corpus.Write(dir, GenFormat.Cf32);

        AboveDcFraction(Path.Combine(dir, "tone-plus3k.cf32")).Should().BeGreaterThan(0.9);

        foreach (string other in (string[])["tone-minus3k", "twotone-2k-5k", "noise-3k", "staircase-10k"])
        {
            AboveDcFraction(Path.Combine(dir, $"{other}.cf32")).Should().BeLessThan(0.05,
                $"{other} must sit in the half that transmits");
        }
    }

    [Fact]
    public void The_corpus_is_reproducible_byte_for_byte()
    {
        // The binaries are gitignored on the promise that regenerating them gives the same files.
        string a = TempDir();
        string b = TempDir();
        Corpus.Write(a, GenFormat.Cf32);
        Corpus.Write(b, GenFormat.Cf32);

        foreach (string file in Directory.GetFiles(a, "*.cf32"))
        {
            File.ReadAllBytes(file).Should().Equal(
                File.ReadAllBytes(Path.Combine(b, Path.GetFileName(file))), Path.GetFileName(file));
        }
    }

    [Fact]
    public void A_sigmf_corpus_pairs_every_data_file_with_readable_metadata()
    {
        string dir = TempDir();
        Corpus.Write(dir, GenFormat.Cf32, sigmf: true);

        string[] data = Directory.GetFiles(dir, "*.sigmf-data");
        data.Should().HaveCount(8);

        foreach (string file in data)
        {
            // Round-trip across the tool boundary: what flex-iq-gen writes, flex-iq-tx must read.
            string? meta = TxMeta.FindBeside(file);
            meta.Should().NotBeNull(Path.GetFileName(file));

            TxMeta parsed = TxMeta.Read(meta!);
            parsed.SampleRate.Should().Be(Signals.SampleRate);
            parsed.Format.Should().Be(M0LTE.Flex.Tools.IqTx.IqFormat.Cf32);
            parsed.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void A_cs16_corpus_declares_itself_as_cs16()
    {
        string dir = TempDir();
        Corpus.Write(dir, GenFormat.Cs16, sigmf: true);

        string file = Directory.GetFiles(dir, "*.sigmf-data")[0];
        TxMeta parsed = TxMeta.Read(TxMeta.FindBeside(file)!);
        parsed.Format.Should().Be(M0LTE.Flex.Tools.IqTx.IqFormat.Cs16);
    }

    private static double AboveDcFraction(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int count = bytes.Length / 8;
        var iq = new float[count * 2];
        Buffer.BlockCopy(bytes, 0, iq, 0, bytes.Length);

        double above = 0;
        double below = 0;
        for (double hz = 250; hz < 12000; hz += 250)
        {
            above += Power(iq, hz);
            below += Power(iq, -hz);
        }

        return above / Math.Max(above + below, 1e-30);
    }

    private static double Power(float[] iq, double hz)
    {
        double re = 0;
        double im = 0;
        int count = Math.Min(iq.Length / 2, 24000);
        for (int n = 0; n < count; n++)
        {
            (double sin, double cos) = Math.SinCos(-2 * Math.PI * hz * n / Signals.SampleRate);
            re += (iq[2 * n] * cos) - (iq[(2 * n) + 1] * sin);
            im += (iq[2 * n] * sin) + (iq[(2 * n) + 1] * cos);
        }

        return ((re * re) + (im * im)) / ((double)count * count);
    }
}
