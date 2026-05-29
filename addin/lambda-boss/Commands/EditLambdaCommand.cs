using ExcelDna.Integration;
using LambdaBoss.Common;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

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
            var formula = activeCell?.Formula2 as string;

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
                activeCell!.Formula2 = letFormula;
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
        // formula is non-null after IsNullOrEmpty guard; net48's BCL
        // doesn't annotate IsNullOrEmpty with [NotNullWhen(false)], so
        // the analyzer can't narrow on its own.
        var closeParen = LetParser.FindMatchingClose(formula!, openParen);
        if (closeParen < 0)
            return null;

        for (var i = closeParen + 1; i < formula!.Length; i++)
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
    ///     every one of the LAMBDA's parameters to a concrete value. When the
    ///     LAMBDA body is itself a LET, its bindings are folded into the outer
    ///     LET so the result is a single flat LET rather than a LET-inside-LET.
    ///     Output is formatted with newlines so it renders legibly in Excel's
    ///     formula bar. Throws when the caller passed more arguments than the
    ///     LAMBDA declares.
    ///     <para>
    ///         Optional arguments the caller omitted still need a value, because
    ///         the body references them — both directly and via
    ///         <c>ISOMITTED(p)</c>, which is meaningless outside a LAMBDA and so
    ///         would make the LET invalid. Each omitted parameter is bound to a
    ///         default extracted from the canonical
    ///         <c>IF(ISOMITTED(p), default, p)</c> wrapper (or <c>NA()</c> when
    ///         no default is discoverable), and every remaining
    ///         <c>ISOMITTED(x)</c> is neutralised to <c>FALSE</c>. The result is
    ///         the plain LET the author started with before LET to LAMBDA was
    ///         run; optionality is not preserved (it cannot be in a LET) and is
    ///         re-applied by re-running LET to LAMBDA.
    ///     </para>
    /// </summary>
    internal static string BuildExpandedLet(LambdaSignature signature, IReadOnlyList<string> arguments)
    {
        if (signature is null) throw new ArgumentNullException(nameof(signature));
        if (arguments is null) throw new ArgumentNullException(nameof(arguments));

        if (arguments.Count > signature.Parameters.Count)
        {
            throw new InvalidOperationException(
                $"Too many arguments: LAMBDA has {signature.Parameters.Count} parameter(s) "
                + $"but {arguments.Count} were provided.");
        }

        // Fold a body that is itself a single LET into the outer binding list.
        List<(string Name, string Value)> folded;
        string body;
        if (TryParseBodyAsLet(signature.Body, out var innerBindings, out var innerBody))
        {
            folded = innerBindings;
            body = innerBody;
        }
        else
        {
            folded = [];
            body = signature.Body;
        }

        // Original (pre-neutralisation) text in which to discover the default
        // expression for omitted parameters. Defaults live inside the canonical
        // IF(ISOMITTED(p), default, p) wrapper, which may sit in any folded
        // binding RHS or the body.
        var corpus = folded.Select(b => b.Value).Append(body).ToList();

        // Leading parameter block. Supplied params are always bound to their
        // argument. Omitted params are bound to their extracted default (or
        // NA()), but only when referenced and not already represented by a
        // same-named folded binding.
        var paramBlock = new List<(string Name, string Value)>();
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var name = signature.Parameters[i];
            var supplied = i < arguments.Count;
            var value = supplied
                ? arguments[i]
                : ExtractIsOmittedDefault(corpus, name) ?? "NA()";

            // A folded binding whose name == param and whose RHS is the param's
            // ISOMITTED wrapper IS the param's binding (the shape
            // LetToLambdaBuilder emits for an optional param). Replace its RHS
            // with the resolved value rather than adding a duplicate — this also
            // removes the self-reference (p, IF(ISOMITTED(p), d, p)) that Excel
            // rejects outright when writing the formula.
            var mergeIndex = folded.FindIndex(b =>
                string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
                && IsIsOmittedWrapperFor(b.Value, name));
            if (mergeIndex >= 0)
            {
                folded[mergeIndex] = (folded[mergeIndex].Name, value);
                continue;
            }

            // A same-named folded binding that isn't a wrapper already binds the
            // name (e.g. a calculation); don't shadow it with a duplicate.
            if (folded.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            // An omitted parameter that nothing references needs no binding.
            if (!supplied && !corpus.Any(t => ReferencesIdentifier(t, name)))
                continue;

            paramBlock.Add((name, value));
        }

        var pairs = paramBlock.Concat(folded).ToList();

        // ISOMITTED is meaningless in a LET, and every parameter now has a
        // concrete value, so neutralise any remaining ISOMITTED(x) to FALSE.
        pairs = pairs.Select(p => (p.Name, NeutralizeIsOmitted(p.Value))).ToList();
        body = NeutralizeIsOmitted(body);

        if (pairs.Count == 0)
            return "=" + body;

        var sb = new StringBuilder();
        sb.Append('=');
        FormulaFormatter.AppendLet(sb, 0, pairs, body);
        return sb.ToString();
    }

    private static readonly Regex IsOmittedCallPattern = new(
        @"^ISOMITTED\s*\(\s*([A-Za-z_][A-Za-z0-9_.?]*)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IsOmittedAnyPattern = new(
        @"ISOMITTED\s*\(\s*[A-Za-z_][A-Za-z0-9_.?]*\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Returns the default expression for <paramref name="param" /> by
    ///     scanning each text for a top-level <c>IF(ISOMITTED(param), default,
    ///     ...)</c> and returning its second argument, or null if none is found.
    ///     String literals are skipped so an IF embedded in a help string is
    ///     ignored. Nested IFs are descended into so a wrapper inside another
    ///     expression is still discovered.
    /// </summary>
    internal static string? ExtractIsOmittedDefault(IEnumerable<string> texts, string param)
    {
        foreach (var text in texts)
        {
            var i = 0;
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    i = SkipString(text, i);
                    continue;
                }

                if (!StartsWithIfOpen(text, i, out var afterOpenParen))
                {
                    i++;
                    continue;
                }

                var openParen = afterOpenParen - 1;
                var closeParen = LetParser.FindMatchingClose(text, openParen);
                if (closeParen < 0)
                {
                    i++;
                    continue;
                }

                var inner = text[(openParen + 1)..closeParen];
                var args = LetParser.SplitTopLevelCommas(inner).Select(a => a.Trim()).ToList();
                if (args.Count == 3 && IsIsOmittedCall(args[0], param))
                    return args[1];

                // Not a match for this param; descend in case a wrapper is
                // nested inside.
                i = afterOpenParen;
            }
        }

        return null;
    }

    /// <summary>
    ///     True when <paramref name="rhs" /> is exactly the canonical optional
    ///     wrapper <c>IF(ISOMITTED(param), default, param)</c>.
    /// </summary>
    private static bool IsIsOmittedWrapperFor(string rhs, string param)
    {
        var trimmed = rhs.Trim();
        if (!StartsWithIfOpen(trimmed, 0, out var afterOpenParen))
            return false;

        var openParen = afterOpenParen - 1;
        var closeParen = LetParser.FindMatchingClose(trimmed, openParen);
        if (closeParen < 0)
            return false;

        for (var i = closeParen + 1; i < trimmed.Length; i++)
            if (!char.IsWhiteSpace(trimmed[i]))
                return false;

        var inner = trimmed[(openParen + 1)..closeParen];
        var args = LetParser.SplitTopLevelCommas(inner).Select(a => a.Trim()).ToList();
        return args.Count == 3
            && IsIsOmittedCall(args[0], param)
            && string.Equals(args[2], param, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIsOmittedCall(string text, string param)
    {
        var match = IsOmittedCallPattern.Match(text.Trim());
        return match.Success
            && string.Equals(match.Groups[1].Value, param, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Replaces every <c>ISOMITTED(identifier)</c> outside a string literal
    ///     with <c>FALSE</c>. ISOMITTED has no meaning in a LET; once every
    ///     parameter is bound, the answer is always "not omitted".
    /// </summary>
    internal static string NeutralizeIsOmitted(string text)
    {
        if (text.IndexOf("ISOMITTED", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

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
            result.Append(IsOmittedAnyPattern.Replace(text[i..segEnd], "FALSE"));
            i = segEnd;
        }

        return result.ToString();
    }

    /// <summary>
    ///     True when <paramref name="ident" /> appears as a whole identifier in
    ///     <paramref name="text" /> outside any string literal.
    /// </summary>
    private static bool ReferencesIdentifier(string text, string ident)
    {
        var pattern = new Regex(
            $@"(?<![A-Za-z0-9_.?])(?:{Regex.Escape(ident)})(?![A-Za-z0-9_.?])",
            RegexOptions.IgnoreCase);

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            var nextQuote = text.IndexOf('"', i);
            var segEnd = nextQuote < 0 ? text.Length : nextQuote;
            if (pattern.IsMatch(text[i..segEnd]))
                return true;
            i = segEnd;
        }

        return false;
    }

    /// <summary>
    ///     True when the text at <paramref name="position" /> starts a
    ///     case-insensitive <c>IF(</c> token with an identifier boundary on the
    ///     left. <paramref name="afterOpenParen" /> is set to the index just
    ///     after the open paren on success.
    /// </summary>
    private static bool StartsWithIfOpen(string text, int position, out int afterOpenParen)
    {
        afterOpenParen = -1;
        if (position + 1 >= text.Length)
            return false;
        if ((text[position] | 0x20) != 'i')
            return false;
        if ((text[position + 1] | 0x20) != 'f')
            return false;
        if (position > 0 && IsIdentifierChar(text[position - 1]))
            return false;

        var j = position + 2;
        while (j < text.Length && char.IsWhiteSpace(text[j]))
            j++;
        if (j >= text.Length || text[j] != '(')
            return false;

        afterOpenParen = j + 1;
        return true;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?';

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