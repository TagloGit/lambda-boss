using System.Text;

namespace LambdaBoss;

/// <summary>
///     Lexical category of a <see cref="FormulaToken" />. The parser only needs
///     coarse categories — it never re-interprets a token's text, so a cell ref,
///     defined name, and function name all arrive as <see cref="Name" /> and are
///     told apart structurally (a <see cref="Name" /> immediately followed by
///     <see cref="LParen" /> is a call; otherwise it is an opaque leaf).
/// </summary>
internal enum FormulaTokenType
{
    /// <summary>A numeric literal, incl. scientific (<c>1.5E-3</c>) and leading-dot (<c>.5</c>).</summary>
    Number,

    /// <summary>A double-quoted string literal, with <c>""</c> escapes.</summary>
    String,

    /// <summary>An error literal (<c>#N/A</c>, <c>#REF!</c>, <c>#DIV/0!</c>, …).</summary>
    Error,

    /// <summary>
    ///     A reference / defined name / function name token — read greedily so a
    ///     sheet-qualified, bracketed, or 3-D reference is a single opaque token
    ///     (<c>'My Sheet'!$A$1</c>, <c>t[[X]:[Y]]</c>, <c>Sheet1:Sheet3!A1</c>).
    /// </summary>
    Name,

    /// <summary>An array constant <c>{1,2;3,4}</c>, kept whole and opaque.</summary>
    Array,

    /// <summary>An operator: <c>^ * / + - &amp; = &lt;&gt; &lt; &lt;= &gt; &gt;= : % # @</c>.</summary>
    Operator,

    /// <summary>An opening parenthesis.</summary>
    LParen,

    /// <summary>A closing parenthesis.</summary>
    RParen,

    /// <summary>An argument / union separator comma.</summary>
    Comma,

    /// <summary>End of input. Carries any trailing whitespace in <see cref="FormulaToken.Lead" />.</summary>
    Eof,
}

/// <summary>
///     A lexed token plus the whitespace that preceded it (<see cref="Lead" />).
///     Keeping leading trivia on every token lets the AST re-serialise the exact
///     source — including cosmetic whitespace, newlines, and the spaces Excel
///     treats as the intersection operator — so a parse → serialise round-trip is
///     character-for-character identical.
/// </summary>
internal sealed class FormulaToken
{
    public FormulaToken(FormulaTokenType type, string lead, string text, int start)
    {
        Type = type;
        Lead = lead;
        Text = text;
        Start = start;
    }

    public FormulaTokenType Type { get; }

    /// <summary>Whitespace immediately preceding <see cref="Text" /> in the source.</summary>
    public string Lead { get; }

    /// <summary>The exact source text of the token (no surrounding whitespace).</summary>
    public string Text { get; }

    /// <summary>Index of <see cref="Text" />'s first character in the source (after <see cref="Lead" />).</summary>
    public int Start { get; }

    /// <summary>Index one past <see cref="Text" />'s last character.</summary>
    public int End => Start + Text.Length;

    internal void Write(StringBuilder sb) => sb.Append(Lead).Append(Text);
}

/// <summary>
///     A node in the parsed expression tree produced by <see cref="FormulaParser" />.
///     Every node can re-emit its exact source via <see cref="Write" /> /
///     <see cref="ToFormula" />, and exposes its <see cref="Start" />/<see cref="End" />
///     span (excluding leading whitespace) for callers that splice source text.
/// </summary>
internal abstract class FormulaNode
{
    /// <summary>Index of the node's first source character (after its leading whitespace).</summary>
    public abstract int Start { get; }

    /// <summary>Index one past the node's last source character.</summary>
    public abstract int End { get; }

    /// <summary>Re-emits the node (including its first token's leading whitespace) into <paramref name="sb" />.</summary>
    internal abstract void Write(StringBuilder sb);

    /// <summary>The node's source text, including the leading whitespace of its first token.</summary>
    public string ToFormula()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

/// <summary>
///     An opaque atomic leaf — a cell reference, structured table reference,
///     defined name, numeric/string/boolean literal, error literal, or array
///     constant. Leaves are never descended into and never become LET steps.
/// </summary>
internal sealed class LeafNode : FormulaNode
{
    public LeafNode(FormulaToken token) => Token = token;

    public FormulaToken Token { get; }

    /// <summary>The lexical category of the leaf's single token.</summary>
    public FormulaTokenType TokenType => Token.Type;

    /// <summary>The leaf's exact text (no leading whitespace).</summary>
    public string Text => Token.Text;

    public override int Start => Token.Start;
    public override int End => Token.End;

    internal override void Write(StringBuilder sb) => Token.Write(sb);
}

/// <summary>
///     A function-call node: a name token immediately followed by a
///     parenthesised, comma-separated argument list. Empty argument slots
///     (<c>SUM(A1,,A3)</c>) are present as <see cref="EmptyArgNode" /> entries so
///     <see cref="Arguments" /> and <see cref="Commas" /> stay positionally aligned.
/// </summary>
internal sealed class FunctionCallNode : FormulaNode
{
    public FunctionCallNode(
        FormulaToken nameToken,
        FormulaToken openParen,
        IReadOnlyList<FormulaNode> arguments,
        IReadOnlyList<FormulaToken> commas,
        FormulaToken closeParen)
    {
        NameToken = nameToken;
        OpenParen = openParen;
        Arguments = arguments;
        Commas = commas;
        CloseParen = closeParen;
    }

    public FormulaToken NameToken { get; }

    /// <summary>The (possibly sheet-qualified) function name as written.</summary>
    public string Name => NameToken.Text;

    public FormulaToken OpenParen { get; }
    public IReadOnlyList<FormulaNode> Arguments { get; }
    public IReadOnlyList<FormulaToken> Commas { get; }
    public FormulaToken CloseParen { get; }

    public override int Start => NameToken.Start;
    public override int End => CloseParen.End;

    internal override void Write(StringBuilder sb)
    {
        NameToken.Write(sb);
        OpenParen.Write(sb);
        WriteSeparatedList(sb, Arguments, Commas);
        CloseParen.Write(sb);
    }

    internal static void WriteSeparatedList(
        StringBuilder sb,
        IReadOnlyList<FormulaNode> items,
        IReadOnlyList<FormulaToken> commas)
    {
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Write(sb);
            if (i < commas.Count)
                commas[i].Write(sb);
        }
    }
}

/// <summary>
///     A binary-operator expression: arithmetic (<c>+ - * / ^</c>), concatenation
///     (<c>&amp;</c>), comparison (<c>= &lt;&gt; …</c>), the range operator
///     (<c>:</c>), and the intersection operator (a single space). Intersection
///     has no operator token — the space is carried as the right operand's leading
///     whitespace — so <see cref="OperatorToken" /> is <c>null</c> in that case.
/// </summary>
internal sealed class BinaryNode : FormulaNode
{
    public BinaryNode(FormulaNode left, string @operator, FormulaToken? operatorToken, FormulaNode right)
    {
        Left = left;
        Operator = @operator;
        OperatorToken = operatorToken;
        Right = right;
    }

    public FormulaNode Left { get; }

    /// <summary>The operator symbol — e.g. <c>+</c>, <c>&lt;=</c>, <c>:</c>, or a single space for intersection.</summary>
    public string Operator { get; }

    /// <summary>The operator's token, or <c>null</c> for the space-as-operator intersection.</summary>
    public FormulaToken? OperatorToken { get; }

    public FormulaNode Right { get; }

    public override int Start => Left.Start;
    public override int End => Right.End;

    internal override void Write(StringBuilder sb)
    {
        Left.Write(sb);
        OperatorToken?.Write(sb);
        Right.Write(sb);
    }
}

/// <summary>
///     A prefix-operator expression: unary plus/minus (<c>-A1</c>, <c>+x</c>) or
///     the implicit-intersection prefix (<c>@A1</c>). Unary expressions are never
///     LET steps — they stay inline with their operand.
/// </summary>
internal sealed class UnaryNode : FormulaNode
{
    public UnaryNode(FormulaToken operatorToken, FormulaNode operand)
    {
        OperatorToken = operatorToken;
        Operand = operand;
    }

    public FormulaToken OperatorToken { get; }

    /// <summary>The operator symbol — <c>+</c>, <c>-</c>, or <c>@</c>.</summary>
    public string Operator => OperatorToken.Text;

    public FormulaNode Operand { get; }

    public override int Start => OperatorToken.Start;
    public override int End => Operand.End;

    internal override void Write(StringBuilder sb)
    {
        OperatorToken.Write(sb);
        Operand.Write(sb);
    }
}

/// <summary>
///     A postfix-operator expression: percent (<c>50%</c>) or spill (<c>A1#</c>).
///     Postfix expressions are never LET steps — they stay inline with their operand.
/// </summary>
internal sealed class PostfixNode : FormulaNode
{
    public PostfixNode(FormulaNode operand, FormulaToken operatorToken)
    {
        Operand = operand;
        OperatorToken = operatorToken;
    }

    public FormulaNode Operand { get; }
    public FormulaToken OperatorToken { get; }

    /// <summary>The operator symbol — <c>%</c> or <c>#</c>.</summary>
    public string Operator => OperatorToken.Text;

    public override int Start => Operand.Start;
    public override int End => OperatorToken.End;

    internal override void Write(StringBuilder sb)
    {
        Operand.Write(sb);
        OperatorToken.Write(sb);
    }
}

/// <summary>
///     A parenthesised group. A single item is ordinary grouping (<c>(a + b)</c>);
///     multiple comma-separated items are the union reference operator
///     (<c>(A1:A3,B1:B3)</c>), kept structurally distinct from a function-call
///     argument list by the absence of a leading name.
/// </summary>
internal sealed class ParenNode : FormulaNode
{
    public ParenNode(
        FormulaToken openParen,
        IReadOnlyList<FormulaNode> items,
        IReadOnlyList<FormulaToken> commas,
        FormulaToken closeParen)
    {
        OpenParen = openParen;
        Items = items;
        Commas = commas;
        CloseParen = closeParen;
    }

    public FormulaToken OpenParen { get; }
    public IReadOnlyList<FormulaNode> Items { get; }
    public IReadOnlyList<FormulaToken> Commas { get; }
    public FormulaToken CloseParen { get; }

    /// <summary>True when the parens hold a comma-separated union of references.</summary>
    public bool IsUnion => Items.Count > 1;

    public override int Start => OpenParen.Start;
    public override int End => CloseParen.End;

    internal override void Write(StringBuilder sb)
    {
        OpenParen.Write(sb);
        FunctionCallNode.WriteSeparatedList(sb, Items, Commas);
        CloseParen.Write(sb);
    }
}

/// <summary>
///     A missing argument slot, e.g. the middle argument of <c>SUM(A1,,A3)</c>.
///     Writes nothing; any whitespace around it is carried by the surrounding
///     comma / close-paren tokens.
/// </summary>
internal sealed class EmptyArgNode : FormulaNode
{
    public EmptyArgNode(int position) => Position = position;

    /// <summary>The source index where the empty slot sits.</summary>
    public int Position { get; }

    public override int Start => Position;
    public override int End => Position;

    internal override void Write(StringBuilder sb)
    {
        // Intentionally emits nothing.
    }
}

/// <summary>
///     A fully parsed formula: the leading <c>=</c> marker (if present), the root
///     expression, and any trailing whitespace. <see cref="ToFormula" /> reproduces
///     the original source character-for-character.
/// </summary>
internal sealed class FormulaAst
{
    public FormulaAst(string source, FormulaToken? equalsToken, FormulaNode root, FormulaToken eofToken)
    {
        Source = source;
        EqualsToken = equalsToken;
        Root = root;
        EofToken = eofToken;
    }

    /// <summary>The original formula text, exactly as parsed.</summary>
    public string Source { get; }

    /// <summary>The leading <c>=</c> marker token, or <c>null</c> if the input had none.</summary>
    public FormulaToken? EqualsToken { get; }

    /// <summary>The root expression node.</summary>
    public FormulaNode Root { get; }

    /// <summary>The end-of-input token; its <see cref="FormulaToken.Lead" /> holds any trailing whitespace.</summary>
    public FormulaToken EofToken { get; }

    /// <summary>Re-serialises the AST. For a successful parse this equals <see cref="Source" />.</summary>
    public string ToFormula()
    {
        var sb = new StringBuilder(Source.Length);
        EqualsToken?.Write(sb);
        Root.Write(sb);
        // Trailing whitespace is carried as the EOF token's leading trivia.
        sb.Append(EofToken.Lead);
        return sb.ToString();
    }
}
