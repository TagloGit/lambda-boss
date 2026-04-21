using System.Text.RegularExpressions;

namespace LambdaBoss;

public record LambdaSignature(IReadOnlyList<string> Parameters, string Body);

/// <summary>
///     Parses an Excel <c>=LAMBDA(param1, ..., paramN, body)</c> formula into
///     its parameter names and final body expression. Mirrors
///     <see cref="LetParser" />. Optional parameters wrapped in
///     <c>[brackets]</c> are accepted and the brackets are stripped from the
///     returned names.
/// </summary>
public static class LambdaSignatureParser
{
    private static readonly Regex LambdaPrefix = new(
        @"^=\s*LAMBDA\s*\(",
        RegexOptions.IgnoreCase);

    private static readonly Regex ParamNamePattern = new(
        @"^\[?([A-Za-z_][A-Za-z0-9_.]*)\]?$");

    public static bool IsLambdaFormula(string? formula)
    {
        if (string.IsNullOrEmpty(formula))
            return false;
        return LambdaPrefix.IsMatch(formula);
    }

    public static LambdaSignature Parse(string formula)
    {
        if (formula is null)
            throw new ArgumentNullException(nameof(formula));

        var match = LambdaPrefix.Match(formula);
        if (!match.Success)
            throw new FormatException("Formula must start with '=LAMBDA('.");

        var openParen = match.Index + match.Length - 1;
        var closeParen = LetParser.FindMatchingClose(formula, openParen);
        if (closeParen < 0)
            throw new FormatException("Unbalanced parentheses in LAMBDA formula.");

        var inner = formula[(openParen + 1)..closeParen];
        var args = LetParser.SplitTopLevelCommas(inner);

        if (args.Count < 1)
            throw new FormatException("LAMBDA must have at least a body expression.");

        var parameters = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            var raw = args[i].Trim();
            var m = ParamNamePattern.Match(raw);
            if (!m.Success)
                throw new FormatException($"Invalid LAMBDA parameter name: '{raw}'.");
            parameters.Add(m.Groups[1].Value);
        }

        var body = args[^1].Trim();
        return new LambdaSignature(parameters, body);
    }
}
