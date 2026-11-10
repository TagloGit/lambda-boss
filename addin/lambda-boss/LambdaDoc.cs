using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>One LAMBDA parameter as its file's header documents it.</summary>
public sealed record LambdaParamDoc(string Name, bool Required, string Description);

/// <summary>
///     What a .lambda file's header says about its function: summary, output and parameters.
///     The argument hints (spec 0013) and the popup read it; the formula carries none of it.
/// </summary>
public sealed record LambdaDoc(
    string Name,
    string Summary,
    string Returns,
    IReadOnlyList<LambdaParamDoc> Parameters,
    string Example);

/// <summary>
///     Reads the header metadata of a .lambda file (spec 0013):
///     <code>
///     /*  FUNCTION NAME:      TLOOKUP
///         DESCRIPTION:*//**Looks up a value ...*/
///     /*  RETURNS:            the value under HeaderName ...
///         PARAMETERS:
///             Table           (Required) Full table including its header row ...
///             Item            (Optional) Value to find ...
///         EXAMPLE:            =TLOOKUP(...) -> 42
///         REVISIONS:          ...
///     */
///     </code>
///     Parameter names and order come from the LAMBDA itself; the header supplies the text.
/// </summary>
public static class LambdaDocParser
{
    // A section key, optionally opening a comment block: "/*  RETURNS:   text" or "    EXAMPLE: text".
    private static readonly Regex KeyLine = new(@"^\s*(?:/\*)?\s*([A-Z][A-Z ]*[A-Z]):[ \t]*(.*)$");

    private static readonly Regex ParamLine = new(
        @"^\s+([A-Za-z_][A-Za-z0-9_.?]*)\s+\((Required|Optional)\)\s*(.*)$",
        RegexOptions.IgnoreCase);

    /// <summary>
    ///     Parses the header of a .lambda file. Never throws on a missing section: an absent
    ///     RETURNS is empty and an undocumented parameter gets an empty description.
    /// </summary>
    /// <exception cref="FormatException">The file has no parsable LAMBDA.</exception>
    public static LambdaDoc Parse(string content)
    {
        var (name, formula) = LambdaParser.Parse(content);
        var signature = LambdaSignatureParser.Parse(formula);
        var header = HeaderText(content);

        var returns = "";
        var example = "";
        var documented = new Dictionary<string, LambdaParamDoc>(StringComparer.OrdinalIgnoreCase);

        string? section = null;
        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var key = KeyLine.Match(line);
            if (key.Success)
            {
                section = key.Groups[1].Value;
                var value = key.Groups[2].Value.Trim();
                if (section == "RETURNS")
                    returns = value;
                else if (section == "EXAMPLE")
                    example = value;
                continue;
            }

            if (section != "PARAMETERS")
                continue;

            var p = ParamLine.Match(line);
            if (p.Success)
            {
                var paramName = p.Groups[1].Value;
                documented[paramName] = new LambdaParamDoc(
                    paramName,
                    string.Equals(p.Groups[2].Value, "Required", StringComparison.OrdinalIgnoreCase),
                    p.Groups[3].Value.Trim());
            }
        }

        var parameters = signature.Parameters
            .Select(param => documented.TryGetValue(param, out var doc)
                ? doc with { Name = param }
                : new LambdaParamDoc(param, false, ""))
            .ToList();

        return new LambdaDoc(
            name,
            LambdaParser.ExtractDescription(content) ?? "",
            returns,
            parameters,
            example);
    }

    /// <summary>
    ///     The documented parameter names in header order, for the format check.
    /// </summary>
    internal static IReadOnlyList<string> DocumentedParameterNames(string content)
    {
        var names = new List<string>();
        string? section = null;
        foreach (var rawLine in HeaderText(content).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var key = KeyLine.Match(line);
            if (key.Success)
            {
                section = key.Groups[1].Value;
                continue;
            }

            if (section == "PARAMETERS")
            {
                var p = ParamLine.Match(line);
                if (p.Success)
                    names.Add(p.Groups[1].Value);
            }
        }

        return names;
    }

    // Everything before the "Name = LAMBDA(" line: the header comments.
    private static string HeaderText(string content)
    {
        var m = Regex.Match(content, @"^\s*\w+\s*=\s*LAMBDA\s*\(", RegexOptions.Multiline);
        return m.Success ? content[..m.Index] : content;
    }
}
