using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Rewrites LAMBDA formula text to apply a prefix to function references.
///     For example, with prefix "tst" and names ["Double", "Triple"],
///     "Double(x)" becomes "tst.Double(x)".
///     String literals (delimited by " with "" as escape) are preserved unchanged.
/// </summary>
public static class PrefixRewriter
{
    /// <summary>
    ///     Applies a prefix to all occurrences of known function names in a formula.
    /// </summary>
    /// <param name="formula">The formula text (may include = prefix).</param>
    /// <param name="prefix">The prefix to apply (e.g. "tst").</param>
    /// <param name="knownNames">The set of function (LAMBDA) names to prefix.</param>
    /// <returns>The rewritten formula with prefixed function names.</returns>
    public static string Apply(string formula, string prefix, IReadOnlyCollection<string> knownNames)
        => Apply(formula, prefix, knownNames, Array.Empty<string>());

    /// <summary>
    ///     Applies a prefix to known function (LAMBDA) names and constant names in a formula.
    ///     Function names are prefixed only when followed by <c>(</c> (a call); constant names
    ///     are prefixed on a bare word boundary, since constants are referenced without parens.
    ///     String literals (delimited by " with "" as escape) are preserved unchanged.
    /// </summary>
    /// <param name="formula">The formula text (may include = prefix).</param>
    /// <param name="prefix">The prefix to apply (e.g. "tst").</param>
    /// <param name="lambdaNames">Function (LAMBDA) names to prefix when followed by <c>(</c>.</param>
    /// <param name="constNames">Constant names to prefix on a bare word boundary.</param>
    /// <returns>The rewritten formula with prefixed names.</returns>
    public static string Apply(
        string formula,
        string prefix,
        IReadOnlyCollection<string> lambdaNames,
        IReadOnlyCollection<string> constNames)
    {
        if (string.IsNullOrEmpty(prefix) || (lambdaNames.Count == 0 && constNames.Count == 0))
            return formula;

        // Build one alternation: LAMBDA names require a trailing "(" (a call); constant
        // names match on a bare word boundary (no parens). The single capture group holds
        // whichever name matched, so the replacement is "{prefix}.$1" for both classes.
        var alternatives = new List<string>();
        if (lambdaNames.Count > 0)
            alternatives.Add($"(?:{string.Join("|", lambdaNames.Select(Regex.Escape))})(?=\\s*\\()");
        if (constNames.Count > 0)
            alternatives.Add($"(?:{string.Join("|", constNames.Select(Regex.Escape))})(?!\\w)");

        var pattern = $@"(?<!\w)({string.Join("|", alternatives)})";
        var nameRegex = new Regex(pattern, RegexOptions.IgnoreCase);

        var result = new StringBuilder();
        var i = 0;

        while (i < formula.Length)
        {
            // Check for string literal
            if (formula[i] == '"')
            {
                result.Append('"');
                i++;
                // Copy string literal contents verbatim
                while (i < formula.Length)
                {
                    if (formula[i] == '"')
                    {
                        result.Append('"');
                        i++;
                        // Doubled quote — escaped, still inside string
                        if (i < formula.Length && formula[i] == '"')
                        {
                            result.Append('"');
                            i++;
                            continue;
                        }
                        // End of string literal
                        break;
                    }
                    result.Append(formula[i]);
                    i++;
                }
                continue;
            }

            // Outside a string literal — find the next string literal or end
            var nextQuote = formula.IndexOf('"', i);
            var segment = nextQuote >= 0 ? formula[i..nextQuote] : formula[i..];

            // Apply prefix rewriting to this non-string segment
            var rewritten = nameRegex.Replace(segment, $"{prefix}.$1");
            result.Append(rewritten);

            i += segment.Length;
        }

        return result.ToString();
    }
}
