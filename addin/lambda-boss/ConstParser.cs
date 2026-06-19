using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Parses .const files to extract the constant name and its literal value.
///     A constant is a defined-name whose RefersTo is a literal scalar or array
///     constant (e.g. <c>={"↑";"↓";…}</c>) — never a LAMBDA. It mirrors the
///     .lambda header convention but carries no LAMBDA/help machinery.
/// </summary>
public static class ConstParser
{
    // Matches: Name = <expr>; on the first non-comment statement (after stripping
    // comments). The value group is greedy so the trailing ; binds to the final
    // statement terminator, leaving internal array-row separators (also ;) intact.
    private static readonly Regex NamePattern = new(
        @"^\s*(\w+)\s*=\s*(.+);\s*$",
        RegexOptions.Singleline);

    /// <summary>
    ///     Parses a .const file and returns the constant name and formula.
    ///     The formula is returned with an = prefix, ready for Name Manager injection.
    /// </summary>
    /// <param name="content">The raw text content of a .const file.</param>
    /// <returns>The parsed name and formula (e.g. "DEFAULTARROWS", "={\"↑\";\"↓\"}").</returns>
    /// <exception cref="FormatException">Thrown when the file cannot be parsed or the RHS is a LAMBDA.</exception>
    public static (string Name, string Formula) Parse(string content)
    {
        // Strip block comments /* ... */ then line comments // ... (same as LambdaParser).
        var stripped = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        stripped = Regex.Replace(stripped, @"//[^\r\n]*", "");
        stripped = stripped.Trim();

        var match = NamePattern.Match(stripped);
        if (!match.Success)
            throw new FormatException("Could not find 'Name = <expr>;' pattern in .const file.");

        var name = match.Groups[1].Value;

        // Collapse the value to a single line, mirroring LambdaParser's whitespace handling
        // so a multi-line array constant injects cleanly.
        var rawExpr = match.Groups[2].Value;
        var lines = rawExpr.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0);
        var expr = string.Join(" ", lines).Trim();

        if (Regex.IsMatch(expr, @"^LAMBDA\s*\(", RegexOptions.IgnoreCase))
            throw new FormatException(
                "A .const RHS must be a literal value, not a LAMBDA. Use a .lambda file instead.");

        return (name, "=" + expr);
    }

    /// <summary>
    ///     Parses a .const file from disk.
    /// </summary>
    public static (string Name, string Formula) ParseFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return Parse(content);
    }
}
