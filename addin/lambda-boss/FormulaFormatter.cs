using System.Text;

namespace LambdaBoss;

/// <summary>
///     Emits formatted <c>LET(...)</c> and <c>LAMBDA(...)</c> blocks into a
///     <see cref="StringBuilder" /> so authored formulas render legibly in
///     Excel's formula bar (one parameter/binding per line, 4-space indent
///     per nesting level). Excel preserves embedded newlines in cell formulas
///     and <c>Name.RefersTo</c> values.
/// </summary>
internal static class FormulaFormatter
{
    internal const int IndentStep = 4;

    /// <summary>
    ///     Emits <c>LET(\n    name1, value1,\n    ..., body\n)</c> starting at
    ///     the caller's current cursor position. <paramref name="indent" /> is
    ///     the column the closing paren sits at (and the column the opening
    ///     line would start at if the caller were at column 0). Bindings and
    ///     body are emitted at <paramref name="indent" /> + 4.
    /// </summary>
    public static void AppendLet(
        StringBuilder sb,
        int indent,
        IReadOnlyList<(string Name, string Value)> bindings,
        string body)
    {
        var close = new string(' ', indent);
        var inner = new string(' ', indent + IndentStep);

        sb.Append("LET(\n");
        foreach (var (name, value) in bindings)
            sb.Append(inner).Append(name).Append(", ").Append(value).Append(",\n");
        sb.Append(inner).Append(body).Append('\n').Append(close).Append(')');
    }

    /// <summary>
    ///     Emits <c>LAMBDA(\n    param1,\n    ..., body\n)</c>. Parameters are
    ///     placed on their own lines and the body is emitted verbatim at
    ///     <paramref name="indent" /> + 4. If the body is itself an inline
    ///     formatted block, callers should already have produced it with the
    ///     same indent convention.
    /// </summary>
    public static void AppendLambda(
        StringBuilder sb,
        int indent,
        IReadOnlyList<string> parameters,
        string body)
    {
        var close = new string(' ', indent);
        var inner = new string(' ', indent + IndentStep);

        sb.Append("LAMBDA(\n");
        foreach (var p in parameters)
            sb.Append(inner).Append(p).Append(",\n");
        sb.Append(inner).Append(body).Append('\n').Append(close).Append(')');
    }
}
