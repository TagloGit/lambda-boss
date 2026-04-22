using System.Text;
using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     User's decision for a single LET binding whose RHS is a value.
/// </summary>
public record InputChoice(
    string OriginalBindingName,
    string ParamName,
    bool Keep,
    bool IsOptional = false);

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

        var bindingsByName = parsed.Bindings
            .Where(b => !b.IsCalculation)
            .ToDictionary(b => b.Name, b => b, StringComparer.OrdinalIgnoreCase);

        // Kept bindings follow the order of request.Inputs, which lets the
        // caller control the generated LAMBDA's parameter order independently
        // of the LET source order.
        var kept = request.Inputs
            .Where(c => c.Keep)
            .Select(c => (Binding: bindingsByName[c.OriginalBindingName], Choice: c))
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

        // Optional flag only makes sense on kept rows.
        var invalidOptional = request.Inputs
            .FirstOrDefault(c => c.IsOptional && !c.Keep);
        if (invalidOptional != null)
            throw new InvalidOperationException(
                $"Input '{invalidOptional.OriginalBindingName}' is marked optional but not kept.");

        // Build rename map for kept inputs where chosen name differs from original.
        var renames = kept
            .Where(k => !string.Equals(k.Binding.Name, k.Choice.ParamName, StringComparison.Ordinal))
            .ToDictionary(k => k.Binding.Name, k => k.Choice.ParamName,
                StringComparer.OrdinalIgnoreCase);

        // Internal bindings preserve source order; their RHS text has renames
        // applied. For excluded inputs (value bindings the user chose not to
        // keep) the RHS is hardcoded into the LAMBDA body, so cell refs are
        // forced absolute for the same reason as optional defaults — see the
        // note on AbsolutizeCellRefs. Calculation bindings are left untouched
        // to preserve the author's original expression verbatim.
        var keptNames = kept.Select(k => k.Binding.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var internalBindings = parsed.Bindings
            .Where(b => !keptNames.Contains(b.Name))
            .Select(b =>
            {
                var renamed = ApplyRenames(b.RhsText, renames);
                var rhs = b.IsCalculation ? renamed : AbsolutizeCellRefs(renamed);
                return new LetBinding(b.Name, rhs, b.IsCalculation);
            })
            .ToList();

        // Optional bindings wrap each optional kept param with an
        // IF(ISOMITTED(...)) defaulting to the original RHS (with renames).
        // They appear before internal bindings so internal bindings can
        // reference the defaulted value.
        // Optional bindings wrap each optional kept param with an
        // IF(ISOMITTED(...)) defaulting to the original RHS (with renames).
        // They appear before internal bindings so internal bindings can
        // reference the defaulted value. Cell references in the default are
        // forced absolute: when Excel stores a LAMBDA as a workbook Name,
        // relative refs shift by the offset between the active cell at
        // registration time and the calling cell, which in practice baked
        // wrong defaults into the LAMBDA. Absolute refs resolve the same
        // regardless of where the LAMBDA is invoked.
        var optionalBindings = kept
            .Where(k => k.Choice.IsOptional)
            .Select(k => new LetBinding(
                k.Choice.ParamName,
                $"IF(ISOMITTED({k.Choice.ParamName}), {AbsolutizeCellRefs(ApplyRenames(k.Binding.RhsText, renames))}, {k.Choice.ParamName})",
                IsCalculation: false))
            .ToList();

        var body = ApplyRenames(parsed.Body, renames);

        // Optional params wrap their name in [] per Excel's IntelliSense
        // convention; the bare name is still used for inner-LET bindings and
        // body references.
        var paramSignatures = kept
            .Select(k => k.Choice.IsOptional
                ? $"[{k.Choice.ParamName}]"
                : k.Choice.ParamName)
            .ToList();

        var innerBindings = optionalBindings.Concat(internalBindings).ToList();

        string lambdaBody;
        if (innerBindings.Count == 0)
        {
            lambdaBody = body;
        }
        else
        {
            // LET sits one level below the LAMBDA (indent 4); its bindings sit
            // two levels below (indent 8). Pre-format the block and pass it to
            // AppendLambda so the LAMBDA layout stays uniform.
            var bodyBuilder = new StringBuilder();
            FormulaFormatter.AppendLet(
                bodyBuilder,
                FormulaFormatter.IndentStep,
                innerBindings.Select(ib => (ib.Name, ib.RhsText)).ToList(),
                body);
            lambdaBody = bodyBuilder.ToString();
        }

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLambda(sb, indent: 0, paramSignatures, lambdaBody);
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

    /// <summary>
    ///     Forces any A1-style cell reference in <paramref name="text" /> to
    ///     fully absolute form (e.g. <c>A1</c>, <c>$A1</c>, <c>A$1</c> all
    ///     become <c>$A$1</c>). Ranges like <c>A1:B5</c> and sheet-qualified
    ///     refs like <c>Sheet1!A1</c> are handled; tokens inside string
    ///     literals are left alone. Used so baked-in expressions in a
    ///     registered LAMBDA (optional-param defaults and excluded-input
    ///     RHS values) don't shift when the Name is invoked from a cell
    ///     other than the one it was registered against.
    /// </summary>
    internal static string AbsolutizeCellRefs(string text)
    {
        var regex = new Regex(
            @"(?<![A-Za-z0-9_.])(\$?)([A-Za-z]{1,3})(\$?)([0-9]+)(?![A-Za-z0-9_.!])");

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
            var rewritten = regex.Replace(segment,
                m => "$" + m.Groups[2].Value + "$" + m.Groups[4].Value);
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
