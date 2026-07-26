using System.Globalization;
using System.Text;

namespace M0LTE.Flex.Tools.IqGen;

/// <summary>
/// Writes SigMF sidecar metadata, so a file describes itself rather than relying on the reader
/// already knowing its sample rate and sample type.
/// </summary>
/// <remarks>
/// A bare <c>.cf32</c> is byte-compatible with GNU Radio's <c>complex64</c> and opens anywhere, but
/// carries no rate — get that wrong and every frequency read off the file is wrong by the same
/// factor, silently. SigMF (<c>.sigmf-data</c> + <c>.sigmf-meta</c>) is the interchange format that
/// carries it, and is read by GNU Radio, inspectrum and the SigMF tooling directly.
/// </remarks>
internal static class SigMf
{
    /// <summary>The SigMF datatype string for a format — the field a reader keys off.</summary>
    public static string DataType(IqFormat format) => format == IqFormat.Cf32 ? "cf32_le" : "ci16_le";

    /// <summary>The extension SigMF expects for the sample data.</summary>
    public const string DataExtension = "sigmf-data";

    /// <summary>Writes the <c>.sigmf-meta</c> companion for a data file.</summary>
    public static void WriteMeta(string dataPath, IqFormat format, int sampleRate, string description)
    {
        string metaPath = Path.ChangeExtension(dataPath, "sigmf-meta");
        var json = new StringBuilder();
        json.Append(CultureInfo.InvariantCulture, $$"""
            {
              "global": {
                "core:datatype": "{{DataType(format)}}",
                "core:sample_rate": {{sampleRate}},
                "core:version": "1.0.0",
                "core:recorder": "flex-iq-gen",
                "core:description": "{{description.Replace("\"", "'", StringComparison.Ordinal)}}"
              },
              "captures": [
                {
                  "core:sample_start": 0
                }
              ],
              "annotations": []
            }

            """);
        File.WriteAllText(metaPath, json.ToString());
    }
}
