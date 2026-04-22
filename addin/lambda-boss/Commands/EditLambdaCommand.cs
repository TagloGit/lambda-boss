using ExcelDna.Integration;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Taglo.Excel.Common;

namespace LambdaBoss.Commands;

/// <summary>
///     Ribbon handler: when the active cell's formula is exactly a call to a
///     registered LAMBDA (e.g. <c>=MyCalc(A1, B1 + 2)</c>), replaces it with
///     an equivalent <c>=LET(...)</c> that inlines the LAMBDA's parameters
///     (bound to the call-site arguments) followed by the LAMBDA's body. The
///     workbook name definition is left in place so rerunning LET to LAMBDA
///     with the same name overwrites it.
/// </summary>
internal static class EditLambdaCommand
{
    private const string NotALambdaCallMessage =
        "Edit Lambda requires a cell whose formula is exactly a call to a LAMBDA "
        + "(e.g. =MyLambda(A1, B1)).";

    private static readonly Regex CallPrefix = new(
        @"^=\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex NestedLetPrefix = new(
        @"^LET\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Run()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            var workbook = app.ActiveWorkbook;
            if (workbook == null)
            {
                ShowError("No active workbook.");
                return;
            }

            var activeCell = app.ActiveCell;
            var formula = activeCell?.Formula as string;

            var call = TryParseLambdaCall(formula);
            if (call == null)
            {
                ShowError(NotALambdaCallMessage);
                return;
            }

            var refersTo = ResolveName(workbook, call.Name);
            if (!LambdaSignatureParser.IsLambdaFormula(refersTo))
            {
                ShowError(NotALambdaCallMessage);
                return;
            }

            LambdaSignature signature;
            try
            {
                signature = LambdaSignatureParser.Parse(refersTo!);
            }
            catch (FormatException ex)
            {
                ShowError($"Could not parse LAMBDA definition for '{call.Name}': {ex.Message}");
                return;
            }

            string letFormula;
            try
            {
                letFormula = BuildExpandedLet(signature, call.Arguments);
            }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
                return;
            }

            try
            {
                activeCell!.Formula = letFormula;
                Logger.Info($"EditLambda: Expanded '{call.Name}' into LET");
            }
            catch (Exception ex)
            {
                Logger.Error("EditLambda/SetFormula", ex);
                ShowError($"Failed to update cell: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("EditLambda", ex);
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Parses a formula that looks like <c>=Name(args...)</c> into the name
    ///     and top-level arguments. Returns null if the formula is not exactly
    ///     a single call (e.g. has trailing content, missing parens, etc.).
    /// </summary>
    internal static LambdaCall? TryParseLambdaCall(string? formula)
    {
        if (string.IsNullOrEmpty(formula))
            return null;

        var match = CallPrefix.Match(formula);
        if (!match.Success)
            return null;

        var name = match.Groups[1].Value;
        var openParen = match.Index + match.Length - 1;
        var closeParen = LetParser.FindMatchingClose(formula, openParen);
        if (closeParen < 0)
            return null;

        for (var i = closeParen + 1; i < formula.Length; i++)
            if (!char.IsWhiteSpace(formula[i]))
                return null;

        var inner = formula[(openParen + 1)..closeParen];
        var args = inner.Trim().Length == 0
            ? []
            : LetParser.SplitTopLevelCommas(inner).Select(a => a.Trim()).ToList();

        return new LambdaCall(name, args);
    }

    /// <summary>
    ///     Builds a <c>=LET(param1, arg1, ..., body)</c> formula that binds
    ///     as many of the LAMBDA's parameters as call-site arguments were
    ///     provided. When the LAMBDA body is itself a LET, its bindings are
    ///     folded into the outer LET so the result is a single flat LET
    ///     rather than a LET-inside-LET. Output is formatted with newlines so
    ///     it renders legibly in Excel's formula bar. Throws when the caller
    ///     passed more arguments than the LAMBDA declares.
    /// </summary>
    internal static string BuildExpandedLet(LambdaSignature signature, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count > signature.Parameters.Count)
        {
            throw new InvalidOperationException(
                $"Too many arguments: LAMBDA has {signature.Parameters.Count} parameter(s) "
                + $"but {arguments.Count} were provided.");
        }

        var pairs = new List<(string Name, string Value)>();
        for (var i = 0; i < arguments.Count; i++)
            pairs.Add((signature.Parameters[i], arguments[i]));

        string body;
        if (TryParseBodyAsLet(signature.Body, out var innerBindings, out var innerBody))
        {
            pairs.AddRange(innerBindings);
            body = innerBody;
        }
        else
            body = signature.Body;

        if (pairs.Count == 0)
            return "=" + body;

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLet(sb, 0, pairs, body);
        return sb.ToString();
    }

    /// <summary>
    ///     Detects whether <paramref name="body" /> is exactly a single
    ///     <c>LET(...)</c> expression (no leading or trailing content) and
    ///     extracts its bindings and inner body if so. Returns false when the
    ///     body isn't a pure LET or the LET is malformed.
    /// </summary>
    private static bool TryParseBodyAsLet(
        string body,
        out List<(string Name, string Value)> bindings,
        out string innerBody)
    {
        bindings = [];
        innerBody = string.Empty;

        var trimmed = body.TrimStart();
        var leading = body.Length - trimmed.Length;
        var match = NestedLetPrefix.Match(trimmed);
        if (!match.Success)
            return false;

        var openParen = leading + match.Index + match.Length - 1;
        var closeParen = LetParser.FindMatchingClose(body, openParen);
        if (closeParen < 0)
            return false;

        for (var i = closeParen + 1; i < body.Length; i++)
            if (!char.IsWhiteSpace(body[i]))
                return false;

        var inner = body[(openParen + 1)..closeParen];
        var args = LetParser.SplitTopLevelCommas(inner).Select(a => a.Trim()).ToList();
        if (args.Count < 3 || args.Count % 2 == 0)
            return false;

        for (var i = 0; i < args.Count - 1; i += 2)
            bindings.Add((args[i], args[i + 1]));
        innerBody = args[^1];
        return true;
    }

    private static string? ResolveName(dynamic workbook, string name)
    {
        try
        {
            var n = workbook.Names.Item(name);
            return n?.RefersTo as string;
        }
        catch
        {
            return null;
        }
    }

    private static void ShowError(string message)
    {
        try
        {
            MessageBox.Show(message, "Lambda Boss", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            Logger.Info($"ShowError: {message}");
        }
    }

    internal record LambdaCall(string Name, IReadOnlyList<string> Arguments);
}