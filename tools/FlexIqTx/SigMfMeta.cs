using System.Text.Json;

namespace M0LTE.Flex.Tools.IqTx;

/// <summary>
/// What a SigMF sidecar tells us about the samples beside it.
/// </summary>
/// <remarks>
/// The point of reading this rather than trusting flags: a raw IQ file carries no rate, so a stream
/// at the wrong rate transmits happily and scales every frequency in it by the ratio — the signal
/// comes out the wrong width, in the wrong place, with nothing to indicate it. The sidecar lets that
/// be <i>caught</i> instead of assumed.
/// </remarks>
internal sealed record SigMfMeta(IqFormat Format, int SampleRate, string? Description)
{
    /// <summary>The sidecar path for a data file, or null if there isn't one beside it.</summary>
    public static string? FindBeside(string dataPath)
    {
        string candidate = Path.ChangeExtension(dataPath, "sigmf-meta");
        return File.Exists(candidate) ? candidate : null;
    }

    public static SigMfMeta Read(string metaPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metaPath));
        if (!document.RootElement.TryGetProperty("global", out JsonElement global))
        {
            throw new ArgumentException($"{metaPath}: no \"global\" object — not a SigMF sidecar");
        }

        string datatype = global.TryGetProperty("core:datatype", out JsonElement type)
            ? type.GetString() ?? ""
            : throw new ArgumentException($"{metaPath}: no core:datatype");

        IqFormat format = datatype switch
        {
            "cf32_le" => IqFormat.Cf32,
            "ci16_le" => IqFormat.Cs16,

            // Big-endian and the other SigMF types are legal but unsupported here; say which rather
            // than silently misreading the bytes as something else.
            _ => throw new ArgumentException(
                $"{metaPath}: core:datatype \"{datatype}\" is not supported (need cf32_le or ci16_le)"),
        };

        if (!global.TryGetProperty("core:sample_rate", out JsonElement rate))
        {
            throw new ArgumentException($"{metaPath}: no core:sample_rate");
        }

        string? description = global.TryGetProperty("core:description", out JsonElement d)
            ? d.GetString()
            : null;

        return new SigMfMeta(format, (int)Math.Round(rate.GetDouble()), description);
    }
}
