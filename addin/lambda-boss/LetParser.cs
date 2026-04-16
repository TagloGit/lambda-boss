using System.Text.RegularExpressions;

namespace LambdaBoss;

public record LetBinding(string Name, string RhsText, bool IsCalculation);

public record ParsedLet(IReadOnlyList<LetBinding> Bindings, string Body);

/// <summary>
///     Parses an Excel <c>=LET(name1, expr1, ..., body)</c> formula into its
///     bindings and final body expression. RHS text is classified as either a
///     pure value (literal/reference/bare name) or a calculation (contains a
///     function call or operator).
/// </summary>
public static class LetParser
{
    private static readonly Regex LetPrefix = new(
        @"^=\s*LET\s*\(",
        RegexOptions.IgnoreCase);

    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*$");

    public static bool IsLetFormula(string? formula)
    {
        if (string.IsNullOrEmpty(formula))
            return false;
        return LetPrefix.IsMatch(formula);
    }

    public static ParsedLet Parse(string formula)
    {
        if (formula is null)
            throw new ArgumentNullException(nameof(formula));

        var match = LetPrefix.Match(formula);
        if (!match.Success)
            throw new FormatException("Formula must start with '=LET('.");

        var openParen = match.Index + match.Length - 1;
        var closeParen = FindMatchingClose(formula, openParen);
        if (closeParen < 0)
            throw new FormatException("Unbalanced parentheses in LET formula.");

        var inner = formula[(openParen + 1)..closeParen];
        var args = SplitTopLevelCommas(inner);

        if (args.Count < 3 || args.Count % 2 == 0)
        {
            throw new FormatException(
                "LET must have an odd number of arguments (pairs of name/value plus a final body).");
        }

        var bindings = new List<LetBinding>();
        for (var i = 0; i < args.Count - 1; i += 2)
        {
            var name = args[i].Trim();
            var rhs = args[i + 1].Trim();
            if (!IdentifierPattern.IsMatch(name))
                throw new FormatException($"Invalid LET binding name: '{name}'.");
            bindings.Add(new LetBinding(name, rhs, IsCalculation(rhs)));
        }

        var body = args[^1].Trim();
        return new ParsedLet(bindings, body);
    }

    /// <summary>
    ///     An RHS is a calculation if it contains (at top level, outside strings
    ///     and parens) any operator, an identifier immediately followed by '(',
    ///     or whitespace between two operand tokens (intersection operator).
    ///     Unary minus / plus on a single literal does not count.
    /// </summary>
    internal static bool IsCalculation(string rhs)
    {
        if (string.IsNullOrWhiteSpace(rhs))
            return false;

        // Strip a single leading unary + or -.
        var s = rhs.TrimStart();
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
            s = s[1..].TrimStart();

        var i = 0;
        var len = s.Length;
        var afterOperand = false;

        while (i < len)
        {
            var c = s[i];

            if (char.IsWhiteSpace(c))
            {
                var j = i;
                while (j < len && char.IsWhiteSpace(s[j])) j++;
                if (afterOperand && j < len && !IsOperatorChar(s[j]) && s[j] != ')' && s[j] != ',')
                    return true;
                i = j;
                continue;
            }

            if (c == '"')
            {
                i = SkipString(s, i);
                afterOperand = true;
                continue;
            }

            if (c == '(')
                return true;

            if (IsOperatorChar(c))
                return true;

            var start = i;
            while (i < len)
            {
                var ch = s[i];
                if (char.IsWhiteSpace(ch) || IsOperatorChar(ch) || ch == '(' || ch == '"' || ch == ',' || ch == ')')
                    break;
                if (ch == '\'')
                {
                    i++;
                    while (i < len && s[i] != '\'') i++;
                    if (i < len) i++;
                    continue;
                }

                i++;
            }

            var token = s[start..i];

            if (i < len && s[i] == '(' && IsIdentifierStart(token))
                return true;

            afterOperand = true;
        }

        return false;
    }

    private static bool IsIdentifierStart(string token)
    {
        return token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_');
    }

    private static bool IsOperatorChar(char c)
    {
        return c is '+' or '-' or '*' or '/' or '^' or '&' or '=' or '<' or '>' or '%';
    }

    private static int SkipString(string text, int openQuoteIndex)
    {
        var i = openQuoteIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    internal static int FindMatchingClose(string text, int openParenIndex)
    {
        var depth = 0;
        var i = openParenIndex;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }

            i++;
        }

        return -1;
    }

    internal static List<string> SplitTopLevelCommas(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (c == '(' || c == '{' || c == '[')
                depth++;
            else if (c == ')' || c == '}' || c == ']')
                depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }

            i++;
        }

        parts.Add(text[start..]);
        return parts;
    }
}