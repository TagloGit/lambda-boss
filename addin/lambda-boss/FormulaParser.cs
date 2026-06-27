namespace LambdaBoss;

/// <summary>
///     A recursive-descent parser for the canonical US form of an Excel formula —
///     the comma-separated, US-function-named text that <c>Range.Formula2</c>
///     returns. It builds an expression tree (<see cref="FormulaAst" />) for
///     structure-aware rewriting (spec 0009 <c>/Unnest</c>); it never evaluates.
///     The grammar is deliberately conservative: lexing of strings, brackets, and
///     references is exact and opaque (a sheet-qualified / structured / 3-D
///     reference stays a single leaf token), while genuinely ambiguous corners
///     (comma-as-union, space-as-intersection) are accepted structurally without
///     trying to resolve their semantics. A successful parse round-trips
///     character-for-character via <see cref="FormulaAst.ToFormula" />.
///     Operator precedence, tightest-binding first:
///     reference <c>:</c> · intersection (space) · spill <c>#</c> · <c>@</c> ·
///     <c>%</c> · <c>^</c> · unary <c>+ -</c> · <c>* /</c> · <c>+ -</c> ·
///     <c>&amp;</c> · comparisons. Exponentiation is left-associative
///     (<c>2^3^2</c> = <c>(2^3)^2</c>), matching Excel.
/// </summary>
internal static class FormulaParser
{
    private static readonly string[] ErrorLiterals =
    {
        // Longest-first so a prefix match never shadows a longer literal.
        "#GETTING_DATA", "#DIV/0!", "#SPILL!", "#BLOCKED!", "#CONNECT!",
        "#UNKNOWN!", "#EXTERNAL!", "#FIELD!", "#VALUE!", "#NULL!", "#NAME?",
        "#CALC!", "#BUSY!", "#REF!", "#NUM!", "#N/A"
    };

    /// <summary>
    ///     Parses <paramref name="formula" /> (with or without a leading <c>=</c>)
    ///     into an expression tree.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formula" /> is null.</exception>
    /// <exception cref="FormatException">The formula is malformed (unexpected token, unbalanced brackets, …).</exception>
    public static FormulaAst Parse(string formula)
    {
        if (formula is null)
            throw new ArgumentNullException(nameof(formula));

        var tokens = Lex(formula);
        var parser = new Parser(formula, tokens);
        return parser.ParseFormula();
    }

    // ---------------------------------------------------------------- Lexer

    private static List<FormulaToken> Lex(string s)
    {
        var tokens = new List<FormulaToken>();
        var i = 0;
        var n = s.Length;

        while (true)
        {
            var leadStart = i;
            while (i < n && IsWhitespace(s[i])) i++;
            var lead = s[leadStart..i];

            if (i >= n)
            {
                tokens.Add(new FormulaToken(FormulaTokenType.Eof, lead, "", i));
                break;
            }

            var start = i;
            var c = s[i];

            // String literal.
            if (c == '"')
            {
                i = SkipString(s, i);
                tokens.Add(new FormulaToken(FormulaTokenType.String, lead, s[start..i], start));
                continue;
            }

            // Array constant {…} — opaque, balanced, strings skipped inside.
            if (c == '{')
            {
                i = SkipArray(s, i);
                tokens.Add(new FormulaToken(FormulaTokenType.Array, lead, s[start..i], start));
                continue;
            }

            // Error literal, else a bare '#' (spill postfix).
            if (c == '#')
            {
                var err = MatchErrorLiteral(s, i);
                if (err > 0)
                {
                    i += err;
                    tokens.Add(new FormulaToken(FormulaTokenType.Error, lead, s[start..i], start));
                }
                else
                {
                    i++;
                    tokens.Add(new FormulaToken(FormulaTokenType.Operator, lead, "#", start));
                }

                continue;
            }

            if (c == '(')
            {
                i++;
                tokens.Add(new FormulaToken(FormulaTokenType.LParen, lead, "(", start));
                continue;
            }

            if (c == ')')
            {
                i++;
                tokens.Add(new FormulaToken(FormulaTokenType.RParen, lead, ")", start));
                continue;
            }

            if (c == ',')
            {
                i++;
                tokens.Add(new FormulaToken(FormulaTokenType.Comma, lead, ",", start));
                continue;
            }

            // Numeric literal (incl. leading-dot and scientific).
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                i = ReadNumber(s, i);
                tokens.Add(new FormulaToken(FormulaTokenType.Number, lead, s[start..i], start));
                continue;
            }

            // Reference / name / function name — quoted qualifier, leading
            // workbook/structured bracket, or a bare identifier/address.
            if (c == '\'' || c == '[' || IsNameStart(c))
            {
                i = ReadReference(s, i);
                tokens.Add(new FormulaToken(FormulaTokenType.Name, lead, s[start..i], start));
                continue;
            }

            // Multi-character comparison operators.
            if (c == '<')
            {
                if (i + 1 < n && (s[i + 1] == '=' || s[i + 1] == '>'))
                    i += 2;
                else i++;
                tokens.Add(new FormulaToken(FormulaTokenType.Operator, lead, s[start..i], start));
                continue;
            }

            if (c == '>')
            {
                if (i + 1 < n && s[i + 1] == '=')
                    i += 2;
                else i++;
                tokens.Add(new FormulaToken(FormulaTokenType.Operator, lead, s[start..i], start));
                continue;
            }

            // Single-character operators.
            if (IsOperatorChar(c))
            {
                i++;
                tokens.Add(new FormulaToken(FormulaTokenType.Operator, lead, s[start..i], start));
                continue;
            }

            throw new FormatException($"Unexpected character '{c}' at position {i} in formula.");
        }

        return tokens;
    }

    private static bool IsWhitespace(char c)
    {
        return c is ' ' or '\t' or '\r' or '\n';
    }

    private static bool IsOperatorChar(char c)
    {
        return c is '+' or '-' or '*' or '/' or '^' or '&' or '=' or '<' or '>' or ':' or '%' or '@' or ';';
    }

    private static bool IsNameStart(char c)
    {
        return char.IsLetter(c) || c == '_' || c == '$' || c == '?' || c == '\\';
    }

    private static bool IsNameChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?' || c == '$' || c == '\\';
    }

    private static int SkipString(string s, int i)
    {
        // i points at the opening quote.
        i++;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        throw new FormatException("Unterminated string literal in formula.");
    }

    private static int SkipArray(string s, int i)
    {
        // i points at the opening brace. Excel array constants don't nest, but we
        // balance braces defensively and skip string literals inside.
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                i = SkipString(s, i);
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                i++;
                if (depth == 0) return i;
                continue;
            }

            i++;
        }

        throw new FormatException("Unterminated array constant in formula.");
    }

    private static int MatchErrorLiteral(string s, int i)
    {
        foreach (var lit in ErrorLiterals)
            if (i + lit.Length <= s.Length &&
                string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0)
                return lit.Length;

        return 0;
    }

    private static int ReadNumber(string s, int i)
    {
        var n = s.Length;
        while (i < n && char.IsDigit(s[i])) i++;
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsDigit(s[i])) i++;
        }

        // Scientific suffix: only consume 'E' when a valid exponent follows.
        if (i < n && (s[i] == 'E' || s[i] == 'e'))
        {
            var j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            if (j < n && char.IsDigit(s[j]))
            {
                i = j;
                while (i < n && char.IsDigit(s[i])) i++;
            }
        }

        return i;
    }

    /// <summary>
    ///     Reads a maximal reference / name / function-name token: an optional
    ///     quoted or workbook-bracket sheet qualifier, the base address or name,
    ///     trailing structured-reference brackets, and any in-token 3-D sheet
    ///     range (<c>Sheet1:Sheet3!A1</c>). Stops before <c>(</c> so the parser
    ///     can recognise a function call.
    /// </summary>
    private static int ReadReference(string s, int i)
    {
        var n = s.Length;

        // Leading single-quoted qualifier: 'My Sheet'! or '[Book.xlsx]Sheet'!
        if (i < n && s[i] == '\'')
        {
            i = SkipSingleQuoted(s, i);
            if (i < n && s[i] == '!') i++;
        }

        while (i < n)
        {
            var c = s[i];

            if (c == '[')
            {
                i = SkipBrackets(s, i);
                continue;
            }

            if (c == '\'')
            {
                // A further quoted segment (e.g. after a 3-D ':' joiner).
                i = SkipSingleQuoted(s, i);
                continue;
            }

            if (c == '!')
            {
                i++;
                continue;
            }

            if (c == ':')
            {
                // ':' is part of this reference only for a 3-D sheet range —
                // i.e. when a sheet-name-then-'!' follows. Otherwise it is the
                // range operator and belongs to the parser, so stop here.
                if (IsThreeDColon(s, i))
                {
                    i++;
                    continue;
                }

                break;
            }

            if (IsNameChar(c))
            {
                i++;
                continue;
            }

            break;
        }

        return i;
    }

    private static int SkipSingleQuoted(string s, int i)
    {
        // i points at the opening single quote. '' is an escaped literal quote.
        i++;
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        throw new FormatException("Unterminated quoted sheet/workbook name in formula.");
    }

    private static int SkipBrackets(string s, int i)
    {
        // i points at the opening '['. Brackets may nest (t[[A]:[B]]); inside,
        // a leading single quote escapes the next character ('[ '] '# '@ '').
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\'')
            {
                i += 2;
                continue;
            }

            if (c == '[')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ']')
            {
                depth--;
                i++;
                if (depth == 0) return i;
                continue;
            }

            i++;
        }

        throw new FormatException("Unterminated structured-reference bracket in formula.");
    }

    /// <summary>
    ///     Looks ahead from a <c>:</c> to decide whether it joins a 3-D sheet
    ///     range (next comes an optionally-quoted sheet name then <c>!</c>) rather
    ///     than acting as the binary range operator.
    /// </summary>
    private static bool IsThreeDColon(string s, int colonIndex)
    {
        var n = s.Length;
        var j = colonIndex + 1;
        if (j < n && s[j] == '\'')
        {
            try
            {
                j = SkipSingleQuoted(s, j);
            }
            catch (FormatException)
            {
                return false;
            }

            return j < n && s[j] == '!';
        }

        while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.' || s[j] == ' '))
            j++;
        return j < n && s[j] == '!';
    }

    // --------------------------------------------------------------- Parser

    private sealed class Parser
    {
        private readonly string _source;
        private readonly List<FormulaToken> _tokens;
        private int _pos;

        public Parser(string source, List<FormulaToken> tokens)
        {
            _source = source;
            _tokens = tokens;
        }

        private FormulaToken Peek => _tokens[_pos];

        private FormulaToken Advance()
        {
            return _tokens[_pos++];
        }

        private bool IsOp(string symbol)
        {
            return Peek.Type == FormulaTokenType.Operator && Peek.Text == symbol;
        }

        public FormulaAst ParseFormula()
        {
            // An optional leading '=' marks a cell formula; a bare '=' anywhere
            // else is comparison. Only the first token counts as the marker.
            FormulaToken? equals = null;
            if (IsOp("=")) equals = Advance();

            var root = ParseExpression();

            if (Peek.Type != FormulaTokenType.Eof)
                throw new FormatException($"Unexpected '{Peek.Text}' at position {Peek.Start} in formula.");

            return new FormulaAst(_source, equals, root, Peek);
        }

        private FormulaNode ParseExpression()
        {
            return ParseComparison();
        }

        private FormulaNode ParseComparison()
        {
            var left = ParseConcat();
            while (Peek.Type == FormulaTokenType.Operator && IsComparisonOp(Peek.Text))
            {
                var op = Advance();
                var right = ParseConcat();
                left = new BinaryNode(left, op.Text, op, right);
            }

            return left;
        }

        private FormulaNode ParseConcat()
        {
            var left = ParseAdditive();
            while (IsOp("&"))
            {
                var op = Advance();
                var right = ParseAdditive();
                left = new BinaryNode(left, op.Text, op, right);
            }

            return left;
        }

        private FormulaNode ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Advance();
                var right = ParseMultiplicative();
                left = new BinaryNode(left, op.Text, op, right);
            }

            return left;
        }

        private FormulaNode ParseMultiplicative()
        {
            var left = ParseUnary();
            while (IsOp("*") || IsOp("/"))
            {
                var op = Advance();
                var right = ParseUnary();
                left = new BinaryNode(left, op.Text, op, right);
            }

            return left;
        }

        private FormulaNode ParseUnary()
        {
            if (IsOp("+") || IsOp("-"))
            {
                var op = Advance();
                var operand = ParseUnary();
                return new UnaryNode(op, operand);
            }

            return ParseExponent();
        }

        private FormulaNode ParseExponent()
        {
            // Left-associative in Excel: 2^3^2 == (2^3)^2.
            var left = ParsePercent();
            while (IsOp("^"))
            {
                var op = Advance();
                var right = ParsePercent();
                left = new BinaryNode(left, op.Text, op, right);
            }

            return left;
        }

        private FormulaNode ParsePercent()
        {
            var operand = ParseAt();
            while (IsOp("%"))
            {
                var op = Advance();
                operand = new PostfixNode(operand, op);
            }

            return operand;
        }

        private FormulaNode ParseAt()
        {
            if (IsOp("@"))
            {
                var op = Advance();
                var operand = ParseAt();
                return new UnaryNode(op, operand);
            }

            return ParseIntersection();
        }

        private FormulaNode ParseIntersection()
        {
            var left = ParseRange();
            // Two operands with only whitespace between them = intersection.
            while (IsOperandStart(Peek))
            {
                var right = ParseRange();
                left = new BinaryNode(left, " ", null, right);
            }

            return left;
        }

        private FormulaNode ParseRange()
        {
            var left = ParseSpill();
            while (IsOp(":"))
            {
                var op = Advance();
                var right = ParseSpill();
                left = new BinaryNode(left, op.Text, op, right);
            }

            return left;
        }

        private FormulaNode ParseSpill()
        {
            var operand = ParsePrimary();
            while (IsOp("#"))
            {
                var op = Advance();
                operand = new PostfixNode(operand, op);
            }

            return operand;
        }

        private FormulaNode ParsePrimary()
        {
            var t = Peek;
            switch (t.Type)
            {
                case FormulaTokenType.Number:
                case FormulaTokenType.String:
                case FormulaTokenType.Error:
                case FormulaTokenType.Array:
                    Advance();
                    return new LeafNode(t);

                case FormulaTokenType.Name:
                    Advance();
                    if (Peek.Type == FormulaTokenType.LParen)
                        return ParseCall(t);
                    return new LeafNode(t);

                case FormulaTokenType.LParen:
                    return ParseParen();

                default:
                    throw new FormatException(
                        $"Unexpected '{t.Text}' at position {t.Start} in formula.");
            }
        }

        private FunctionCallNode ParseCall(FormulaToken nameToken)
        {
            var (open, items, commas, close) = ParseArgList();
            return new FunctionCallNode(nameToken, open, items, commas, close);
        }

        private ParenNode ParseParen()
        {
            var (open, items, commas, close) = ParseArgList();
            return new ParenNode(open, items, commas, close);
        }

        /// <summary>
        ///     Parses <c>( arg , arg , … )</c> starting at the open paren. Empty
        ///     slots become <see cref="EmptyArgNode" />. Shared by function calls
        ///     and grouping/union parens.
        /// </summary>
        private (FormulaToken Open, List<FormulaNode> Items, List<FormulaToken> Commas, FormulaToken Close)
            ParseArgList()
        {
            var open = Advance(); // '('
            var items = new List<FormulaNode>();
            var commas = new List<FormulaToken>();

            if (Peek.Type == FormulaTokenType.RParen)
            {
                var emptyClose = Advance();
                return (open, items, commas, emptyClose);
            }

            while (true)
            {
                if (Peek.Type == FormulaTokenType.Comma || Peek.Type == FormulaTokenType.RParen)
                    items.Add(new EmptyArgNode(Peek.Start));
                else
                    items.Add(ParseExpression());

                if (Peek.Type == FormulaTokenType.Comma)
                {
                    commas.Add(Advance());
                    continue;
                }

                if (Peek.Type == FormulaTokenType.RParen)
                {
                    var close = Advance();
                    return (open, items, commas, close);
                }

                throw new FormatException(
                    $"Expected ',' or ')' but found '{Peek.Text}' at position {Peek.Start} in formula.");
            }
        }

        private static bool IsComparisonOp(string s)
        {
            return s is "=" or "<>" or "<" or "<=" or ">" or ">=";
        }

        /// <summary>
        ///     True when <paramref name="t" /> can begin an operand — used to spot
        ///     the space-as-intersection operator. Leading <c>+ - @</c> are
        ///     excluded so <c>A1 -B1</c> stays subtraction, not intersection.
        /// </summary>
        private static bool IsOperandStart(FormulaToken t)
        {
            return t.Type is FormulaTokenType.Number
                or FormulaTokenType.String
                or FormulaTokenType.Error
                or FormulaTokenType.Name
                or FormulaTokenType.Array
                or FormulaTokenType.LParen;
        }
    }
}