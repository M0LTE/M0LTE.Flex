namespace M0LTE.Flex.Tests;

/// <summary>
/// Every string this library can hand a caller has to be ASCII.
/// </summary>
/// <remarks>
/// <para>The consumer of a warning or an exception message from here is usually a daemon, and a
/// daemon's output is read in <c>journalctl</c>, whose pager under a C/POSIX locale renders every
/// byte above 0x7F as <c>&lt;E2&gt;&lt;80&gt;&lt;94&gt;</c>. That locale is the default on a
/// minimal Debian install and is what systemd hands the pager when LANG is unset, so it is what a
/// station operator sees. The station is not ours to configure, so the fix belongs here: <c>-</c>
/// for a dash, <c>-&gt;</c> for an arrow.</para>
/// <para>Literals only. Comments are welcome to use whatever punctuation reads best, since none of
/// them is ever printed - which is why this parses the source rather than grepping it. Scoped to
/// <c>src</c>: the tool under <c>tools</c> prints to whatever terminal its operator ran it from,
/// which is a different audience with a different locale.</para>
/// </remarks>
public class PrintableTextTests
{
    [Fact]
    public void No_String_A_Caller_Can_Print_Carries_A_Byte_Above_Ascii()
    {
        var offenders = new List<string>();

        foreach (string file in SourceFiles())
        {
            foreach ((int line, char c) in NonAsciiInLiterals(File.ReadAllText(file)))
            {
                offenders.Add($"{Relative(file)}:{line}: U+{(int)c:X4} '{c}'");
            }
        }

        offenders.Should().BeEmpty(
            "journalctl's pager renders non-ASCII as <XX> hex escapes under a C locale, so "
            + "printable output has to be ASCII (use - for a dash, -> for an arrow)");
    }

    /// <summary>Every source file of the library itself, skipping build output.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        char sep = Path.DirectorySeparatorChar;

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                && !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Every non-ASCII character inside a string or char literal, with its line number. Ported from
    /// the same test in packet-net/pdn-soundmodem, which is where these strings end up being read.
    /// </summary>
    private static IEnumerable<(int Line, char Char)> NonAsciiInLiterals(string text)
    {
        var found = new List<(int, char)>();
        int i = 0, line = 1;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '/' && Next(i) == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && Next(i) == '*')
            {
                for (i += 2; i < text.Length && !(text[i] == '*' && Next(i) == '/'); i++)
                {
                    if (text[i] == '\n') line++;
                }

                i += 2;
            }
            else if (Matches(i, "\"\"\""))
            {
                for (i += 3; i < text.Length && !Matches(i, "\"\"\""); i++) Take();
                i += 3;
            }
            else if (c == '@' && Next(i) == '"')
            {
                for (i += 2; i < text.Length; i++)
                {
                    if (text[i] == '"' && Next(i) != '"') { i++; break; }
                    if (text[i] == '"') i++;          // "" is an escaped quote, not the end
                    Take();
                }
            }
            else if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c && text[i] != '\n'; i++)
                {
                    if (text[i] == '\\') { i++; continue; }
                    Take();
                }

                i++;
            }
            else
            {
                i++;
            }

            void Take()
            {
                if (i >= text.Length) return;
                if (text[i] == '\n') line++;
                else if (text[i] > 127) found.Add((line, text[i]));
            }
        }

        return found;

        char Next(int at) => at + 1 < text.Length ? text[at + 1] : '\0';
        bool Matches(int at, string s) => at + s.Length <= text.Length
                                          && string.CompareOrdinal(text, at, s, 0, s.Length) == 0;
    }

    private static string Relative(string file) => Path.GetRelativePath(FindRepoRoot(), file);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "M0LTE.Flex.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
