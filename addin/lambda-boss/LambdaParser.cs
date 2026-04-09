using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Parses .lambda files to extract the function name and formula body.
/// </summary>
public static class LambdaParser
{
    // Matches: Name = LAMBDA( at the start of a non-comment line (after stripping block comments)
    private static readonly Regex NamePattern = new(
        @"^\s*(\w+)\s*=\s*LAMBDA\s*\(",
        RegexOptions.Multiline);

    /// <summary>
    ///     Parses a .lambda file and returns the function name and formula.
    ///     The formula is returned with an = prefix, ready for Name Manager injection.
    /// </summary>
    /// <param name="content">The raw text content of a .lambda file.</param>
    /// <returns>The parsed name and formula (e.g. "Double", "=LAMBDA(x, x*2)").</returns>
    /// <exception cref="FormatException">Thrown when the file cannot be parsed.</exception>
    public static (string Name, string Formula) Parse(string content)
    {
        // Strip block comments /* ... */
        var stripped = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Strip line comments // ...
        stripped = Regex.Replace(stripped, @"//[^\r\n]*", "");

        var match = NamePattern.Match(stripped);
        if (!match.Success)
            throw new FormatException("Could not find 'Name = LAMBDA(' pattern in .lambda file.");

        var name = match.Groups[1].Value;

        // Extract the formula: everything from LAMBDA( to the matching closing );
        // We need to find the LAMBDA( in the stripped content, then balance parentheses
        var lambdaStart = match.Index + match.Length - 1; // position of the opening (
        var formula = ExtractBalancedFormula(stripped, lambdaStart);

        return (name, "=" + formula);
    }

    /// <summary>
    ///     Parses a .lambda file from disk.
    /// </summary>
    public static (string Name, string Formula) ParseFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return Parse(content);
    }

    private static string ExtractBalancedFormula(string text, int openParenIndex)
    {
        // Find the start of "LAMBDA" before the open paren
        var searchBack = text[..openParenIndex].TrimEnd();
        var lambdaKeywordStart = searchBack.LastIndexOf("LAMBDA", StringComparison.OrdinalIgnoreCase);
        if (lambdaKeywordStart < 0)
            throw new FormatException("Could not locate LAMBDA keyword.");

        var depth = 0;
        var endIndex = -1;

        for (var i = openParenIndex; i < text.Length; i++)
        {
            var c = text[i];

            // Skip string literals (Excel uses "" for escaped quotes inside strings)
            if (c == '"')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        // Check for doubled quote (escaped)
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '(')
                depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    endIndex = i;
                    break;
                }
            }
        }

        if (endIndex < 0)
            throw new FormatException("Unbalanced parentheses in LAMBDA formula.");

        var raw = text[lambdaKeywordStart..(endIndex + 1)];

        // Clean up: collapse excessive whitespace while preserving single spaces/newlines
        // Remove leading/trailing whitespace from each line, collapse blank lines
        var lines = raw.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0);

        return string.Join(" ", lines).Trim();
    }
}
