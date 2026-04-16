using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     User's decision for a single LET binding whose RHS is a value.
/// </summary>
public record InputChoice(string OriginalBindingName, string ParamName, bool Keep);

public record LambdaGenerationRequest(
    string LambdaName,
    ParsedLet ParsedLet,
    IReadOnlyList<InputChoice> Inputs);

public static class LetToLambdaBuilder
{
    public static string Build(LambdaGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parsed = request.ParsedLet;

        var choicesByName = request.Inputs.ToDictionary(
            c => c.OriginalBindingName, c => c, StringComparer.OrdinalIgnoreCase);

        // Validate each value-binding has a matching choice.
        foreach (var b in parsed.Bindings.Where(b => !b.IsCalculation))
        {
            if (!choicesByName.ContainsKey(b.Name))
                throw new InvalidOperationException(
                    $"No input choice provided for binding '{b.Name}'.");
        }

        var kept = parsed.Bindings
            .Where(b => !b.IsCalculation && choicesByName[b.Name].Keep)
            .Select(b => (Binding: b, Choice: choicesByName[b.Name]))
            .ToList();

        // Validate param names: non-empty, unique, no collision with non-kept binding names.
        var paramNames = kept.Select(k => k.Choice.ParamName).ToList();
        if (paramNames.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Parameter names must be non-empty.");
        var dup = paramNames
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
            throw new InvalidOperationException($"Duplicate parameter name: '{dup.Key}'.");

        var internalBindingNames = parsed.Bindings
            .Where(b => b.IsCalculation
                || (choicesByName.TryGetValue(b.Name, out var c) && !c.Keep))
            .Select(b => b.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paramNames)
            if (internalBindingNames.Contains(p))
                throw new InvalidOperationException(
                    $"Parameter name '{p}' collides with a retained LET binding name.");

        // Build rename map for kept inputs where chosen name differs from original.
        var renames = kept
            .Where(k => !string.Equals(k.Binding.Name, k.Choice.ParamName, StringComparison.Ordinal))
            .ToDictionary(k => k.Binding.Name, k => k.Choice.ParamName,
                StringComparer.OrdinalIgnoreCase);

        // Internal bindings preserve source order; their RHS text has renames applied.
        var keptNames = kept.Select(k => k.Binding.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var internalBindings = parsed.Bindings
            .Where(b => !keptNames.Contains(b.Name))
            .Select(b => new LetBinding(b.Name, ApplyRenames(b.RhsText, renames), b.IsCalculation))
            .ToList();

        var body = ApplyRenames(parsed.Body, renames);

        var sb = new StringBuilder();
        sb.Append("=LAMBDA(");
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(kept[i].Choice.ParamName);
        }
        if (kept.Count > 0)
            sb.Append(", ");

        if (internalBindings.Count == 0)
        {
            sb.Append(body);
        }
        else
        {
            sb.Append("LET(");
            foreach (var ib in internalBindings)
            {
                sb.Append(ib.Name).Append(", ").Append(ib.RhsText).Append(", ");
            }
            sb.Append(body).Append(')');
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    ///     Replaces identifier occurrences of each rename key with its value,
    ///     skipping string literals. Case-insensitive, word-boundary matched.
    /// </summary>
    internal static string ApplyRenames(string text, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) return text;

        var pattern = string.Join("|", renames.Keys.Select(Regex.Escape));
        var regex = new Regex($@"(?<![A-Za-z0-9_.]){pattern}(?![A-Za-z0-9_.])",
            RegexOptions.IgnoreCase);

        var result = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                var end = SkipString(text, i);
                result.Append(text, i, end - i);
                i = end;
                continue;
            }

            var nextQuote = text.IndexOf('"', i);
            var segEnd = nextQuote < 0 ? text.Length : nextQuote;
            var segment = text[i..segEnd];
            var rewritten = regex.Replace(segment, m =>
            {
                // Find the rename key case-insensitively.
                var key = renames.Keys.First(k =>
                    string.Equals(k, m.Value, StringComparison.OrdinalIgnoreCase));
                return renames[key];
            });
            result.Append(rewritten);
            i = segEnd;
        }
        return result.ToString();
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
}
