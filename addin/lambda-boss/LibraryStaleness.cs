using System.Text;

namespace LambdaBoss;

/// <summary>
///     Whether a workbook's copy of a library matches the add-in's (#452). A copy loaded from an
///     older library keeps working, wrongly, and nothing on screen says so: Tim lost a drill to a
///     two-day-old Loops on 11 Nov.
/// </summary>
internal static class LibraryStaleness
{
    /// <summary>
    ///     How many of the add-in's names (under the workbook's prefix) are missing from the workbook
    ///     or define something different there. Names the workbook has beyond the library don't count.
    /// </summary>
    /// <param name="workbookNames">The workbook's names stamped for this library: name → RefersTo.</param>
    /// <param name="current">The add-in's library, already prefixed as the workbook loaded it.</param>
    public static (int Differ, int Total) Compare(
        IReadOnlyDictionary<string, string> workbookNames,
        IReadOnlyList<(string Name, string Formula)> current)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, refersTo) in workbookNames)
            byName[StripSheet(name)] = refersTo;

        var differ = 0;
        foreach (var (name, formula) in current)
        {
            if (!byName.TryGetValue(name, out var refersTo)
                || !string.Equals(Normalize(refersTo), Normalize(formula), StringComparison.Ordinal))
                differ++;
        }

        return (differ, current.Count);
    }

    /// <summary>
    ///     Formula text with the leading <c>=</c>, whitespace and letter case outside string literals
    ///     removed, so Excel's own re-spacing or re-casing of a stored name doesn't read as a change.
    /// </summary>
    internal static string Normalize(string formula)
    {
        var sb = new StringBuilder(formula.Length);
        var inString = false;
        var start = formula.StartsWith("=", StringComparison.Ordinal) ? 1 : 0;
        for (var i = start; i < formula.Length; i++)
        {
            var c = formula[i];
            if (c == '"')
            {
                inString = !inString;
                sb.Append(c);
            }
            else if (inString)
                sb.Append(c);
            else if (!char.IsWhiteSpace(c))
                sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    // Excel reports a sheet-scoped name as "Sheet1!Name".
    private static string StripSheet(string name)
    {
        var bang = name.LastIndexOf('!');
        return bang >= 0 ? name.Substring(bang + 1) : name;
    }
}
