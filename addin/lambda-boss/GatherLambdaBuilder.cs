using System.Text;

namespace LambdaBoss;

/// <summary>
///     The two pieces Save writes for a named gather (spec 0019 §3): the LAMBDA to register
///     under the name, and the call that replaces the sink's formula.
/// </summary>
public sealed record GatherLambda(string Name, string Formula, string Call);

/// <summary>
///     Builds <c>=LAMBDA(&lt;inputs&gt;, LET(&lt;steps&gt;, &lt;body&gt;))</c> from a gather's
///     bindings, and the call <c>=name(&lt;input refs&gt;)</c>. Pure text: no Excel, no settings.
///     The parameters are the plain input rows in row order (a range, a cell or a spilled
///     anchor; slices and inner-LET bindings are not parameters), each argument is the row's
///     right-hand side as written (<c>K287#</c>, <c>Q57:T60</c>). Every other binding stays
///     inside the LET, and a LET left with no bindings collapses to just the body, as
///     /LET to LAMBDA does.
/// </summary>
public static class GatherLambdaBuilder
{
    /// <summary>True for a row that becomes a LAMBDA parameter.</summary>
    public static bool IsParameter(BindingRow row) =>
        row.Role == BindingRole.Input && !row.IsExpansion && row.SliceOf == null;

    /// <summary>
    ///     The Lambda name's state for the dialog: null message when it is empty or fine, else
    ///     the text to show. <c>Ok</c> false blocks Save. The checks run in order: Excel name rules,
    ///     at least one input row (a LAMBDA without one can't be built, so Save would silently do
    ///     nothing), then the workbook: an existing LAMBDA of that name is overwritten with a
    ///     warning, any other existing name is refused, as in /LET to LAMBDA.
    /// </summary>
    public static (bool Ok, string? Message, bool IsWarning) ValidateName(
        string name, IReadOnlyList<BindingRow> bindings, Func<string, string?> resolveExistingRefersTo)
    {
        if (name.Length == 0)
            return (true, null, false);

        var validation = ExcelNameValidator.Validate(name);
        if (!validation.IsValid)
            return (false, validation.Error, false);

        if (!bindings.Any(IsParameter))
            return (false, "A LAMBDA needs at least one input row.", false);

        var existing = resolveExistingRefersTo(name);
        if (existing != null)
        {
            if (!LambdaSignatureParser.IsLambdaFormula(existing))
                return (false, $"'{name}' already exists in this workbook and is not a LAMBDA. Choose another name.", false);
            return (true, $"'{name}' already exists — Save will overwrite the existing LAMBDA.", true);
        }

        return (true, null, false);
    }

    /// <summary>
    ///     Returns null when there is no input to make a parameter (a LAMBDA with no
    ///     parameters would just be the formula).
    /// </summary>
    public static GatherLambda? Build(
        string lambdaName, IReadOnlyList<BindingRow> bindings, string body)
    {
        if (lambdaName is null) throw new ArgumentNullException(nameof(lambdaName));
        if (bindings is null) throw new ArgumentNullException(nameof(bindings));
        if (body is null) throw new ArgumentNullException(nameof(body));

        var parameters = bindings.Where(IsParameter).ToList();
        if (parameters.Count == 0)
            return null;

        var inner = bindings.Where(b => !IsParameter(b)).ToList();

        // A reference left in the body or a step (a row the user unticked) would shift
        // with the cell the name was registered from, so it is made absolute, as
        // /LET to LAMBDA does for the bindings it bakes in.
        var letNames = bindings.Select(b => b.Name).ToList();
        string Absolute(string text) => LetToLambdaBuilder.AbsolutizeCellRefs(text, letNames);

        string lambdaBody;
        if (inner.Count == 0)
        {
            lambdaBody = Absolute(body);
        }
        else
        {
            var letBuilder = new StringBuilder();
            FormulaFormatter.AppendLet(
                letBuilder,
                FormulaFormatter.IndentStep,
                inner.Select(b => (b.Name, Absolute(b.Rhs))).ToList(),
                Absolute(body));
            lambdaBody = letBuilder.ToString();
        }

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLambda(
            sb, indent: 0, parameters.Select(p => p.Name).ToList(), lambdaBody);

        var call = "=" + lambdaName + "(" + string.Join(", ", parameters.Select(p => p.Rhs)) + ")";
        return new GatherLambda(lambdaName, sb.ToString(), call);
    }
}
