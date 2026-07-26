using M0LTE.Flex.Tools.IqTx;

namespace M0LTE.Flex.Tools.Tests;

/// <summary>
/// The SigMF sidecar reader and the transmitter's option handling — the guards that turn a silent
/// wrong transmission into a refusal.
/// </summary>
public sealed class SigMfAndOptionsTests
{
    private static string WriteMeta(string datatype, int rate)
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string data = Path.Combine(dir, "x.sigmf-data");
        File.WriteAllBytes(data, new byte[8]);
        File.WriteAllText(
            Path.ChangeExtension(data, "sigmf-meta"),
            "{\"global\":{\"core:datatype\":\"" + datatype + "\",\"core:sample_rate\":" + rate
                + ",\"core:version\":\"1.0.0\"}}");
        return data;
    }

    [Fact]
    public void A_sidecar_is_found_by_basename_whatever_the_data_file_is_called()
    {
        string data = WriteMeta("cf32_le", 24000);
        SigMfMeta.FindBeside(data).Should().NotBeNull();

        // Deliberately not tied to the .sigmf-data extension: a plain foo.cf32 sitting next to a
        // foo.sigmf-meta should still pick it up.
        string renamed = Path.Combine(Path.GetDirectoryName(data)!, "x.cf32");
        File.Copy(data, renamed);
        SigMfMeta.FindBeside(renamed).Should().NotBeNull();

        // But a file with no sidecar of its own must not borrow someone else's.
        string lonely = Path.Combine(Path.GetDirectoryName(data)!, "other.cf32");
        File.Copy(data, lonely);
        SigMfMeta.FindBeside(lonely).Should().BeNull();
    }

    [Fact]
    public void The_datatype_determines_how_the_samples_are_read()
    {
        SigMfMeta cf32 = SigMfMeta.Read(Path.ChangeExtension(WriteMeta("cf32_le", 24000), "sigmf-meta"));
        cf32.Format.Should().Be(IqFormat.Cf32);
        cf32.SampleRate.Should().Be(24000);

        SigMfMeta cs16 = SigMfMeta.Read(Path.ChangeExtension(WriteMeta("ci16_le", 48000), "sigmf-meta"));
        cs16.Format.Should().Be(IqFormat.Cs16);
        cs16.SampleRate.Should().Be(48000);
    }

    [Theory]
    [InlineData("cf32_be")]
    [InlineData("cf64_le")]
    [InlineData("ci8")]
    public void An_unsupported_datatype_is_named_rather_than_misread(string datatype)
    {
        // Reading big-endian or 64-bit samples as little-endian float32 produces plausible-looking
        // garbage rather than an error, so this must refuse rather than guess.
        Action read = () => SigMfMeta.Read(Path.ChangeExtension(WriteMeta(datatype, 24000), "sigmf-meta"));
        read.Should().Throw<ArgumentException>().WithMessage($"*{datatype}*");
    }

    [Fact]
    public void A_sidecar_missing_its_datatype_or_rate_is_rejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string meta = Path.Combine(dir, "x.sigmf-meta");

        File.WriteAllText(meta, """{"global":{"core:sample_rate":24000}}""");
        ((Action)(() => SigMfMeta.Read(meta))).Should().Throw<ArgumentException>().WithMessage("*datatype*");

        File.WriteAllText(meta, """{"global":{"core:datatype":"cf32_le"}}""");
        ((Action)(() => SigMfMeta.Read(meta))).Should().Throw<ArgumentException>().WithMessage("*sample_rate*");

        File.WriteAllText(meta, """{"nope":{}}""");
        ((Action)(() => SigMfMeta.Read(meta))).Should().Throw<ArgumentException>().WithMessage("*SigMF*");
    }

    [Fact]
    public void The_declared_sample_rate_is_carried_so_a_mismatch_can_be_caught()
    {
        // The reason the sidecar is read at all: a 48 kHz capture sent at 24 kHz halves every
        // frequency in the signal and its width, and nothing about the transmission looks wrong.
        SigMfMeta meta = SigMfMeta.Read(Path.ChangeExtension(WriteMeta("cf32_le", 48000), "sigmf-meta"));
        meta.SampleRate.Should().Be(48000).And.NotBe(FlexWaveformIqOutput.SampleRate);
    }

    [Theory]
    [InlineData("14.2", 14.2)]
    [InlineData("14200k", 14.2)]
    [InlineData("14.2M", 14.2)]
    [InlineData("14200000Hz", 14.2)]
    public void Frequencies_accept_a_unit_suffix_and_default_to_MHz(string text, double expectedMhz)
    {
        Options options = Options.Parse(["--freq", text]);
        options.FreqMhz.Should().BeApproximately(expectedMhz, 1e-9);
    }

    [Theory]
    [InlineData("3000", 3000)]
    [InlineData("3k", 3000)]
    [InlineData("3kHz", 3000)]
    public void Bandwidths_default_to_Hz(string text, double expectedHz)
    {
        Options.Parse(["--bw", text]).BandwidthHz.Should().Be(expectedHz);
    }

    [Fact]
    public void A_bandwidth_the_radio_cannot_pass_is_refused_up_front()
    {
        // Better to fail on the command line than to key up and truncate.
        Action parse = () => Options.Parse(["--bw", "15k"]);
        parse.Should().Throw<ArgumentException>().WithMessage("*truncated*");
    }

    [Fact]
    public void The_declared_band_follows_the_reference_convention()
    {
        Options centre = Options.Parse(["--freq", "14.2", "--bw", "4k"]);
        centre.LowMhz.Should().BeApproximately(14.198, 1e-9);
        centre.HighMhz.Should().BeApproximately(14.202, 1e-9);

        Options edge = Options.Parse(["--freq", "14.2", "--bw", "4k", "--reference", "loweredge"]);
        edge.LowMhz.Should().BeApproximately(14.200, 1e-9);
        edge.HighMhz.Should().BeApproximately(14.204, 1e-9);
    }

    [Theory]
    [InlineData("--gain", "0")]
    [InlineData("--power", "200")]
    [InlineData("--reference", "sideways")]
    [InlineData("--format", "cf64")]
    [InlineData("--max-seconds", "-1")]
    public void Nonsense_is_rejected_with_a_reason(string option, string value)
    {
        ((Action)(() => Options.Parse([option, value]))).Should().Throw<ArgumentException>();
    }
}
