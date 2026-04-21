using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

using ExcelDna.Integration;

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

    internal record LambdaCall(string Name, IReadOnlyList<string> Arguments);

    public static void Run()
    {
        try
        {
            dynamic app = ExcelDnaUtil.Application;
            dynamic workbook = app.ActiveWorkbook;
            if (workbook == null)
            {
                ShowError("No active workbook.");
                return;
            }

            dynamic activeCell = app.ActiveCell;
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
                activeCell.Formula = letFormula;
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
        {
            if (!char.IsWhiteSpace(formula[i]))
                return null;
        }

        var inner = formula[(openParen + 1)..closeParen];
        var args = inner.Trim().Length == 0
            ? new List<string>()
            : LetParser.SplitTopLevelCommas(inner).Select(a => a.Trim()).ToList();

        return new LambdaCall(name, args);
    }

    /// <summary>
    ///     Builds a <c>=LET(param1, arg1, ..., body)</c> formula that binds
    ///     as many of the LAMBDA's parameters as call-site arguments were
    ///     provided. Throws when the caller passed more arguments than the
    ///     LAMBDA declares.
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

        if (arguments.Count == 0)
            return "=" + signature.Body;

        var sb = new StringBuilder();
        sb.Append("=LET(");
        for (var i = 0; i < arguments.Count; i++)
        {
            sb.Append(signature.Parameters[i])
              .Append(", ")
              .Append(arguments[i])
              .Append(", ");
        }
        sb.Append(signature.Body).Append(')');
        return sb.ToString();
    }

    private static string? ResolveName(dynamic workbook, string name)
    {
        try
        {
            dynamic n = workbook.Names.Item(name);
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
}
