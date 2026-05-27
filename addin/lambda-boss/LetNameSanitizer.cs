using System.Text;

namespace LambdaBoss;

/// <summary>
///     Turns a free-text label (e.g. a cell-above or cell-left value) into a
///     LET binding name. Splits on runs of non-identifier characters, lowercases
///     the first word's initial letter, TitleCases subsequent words, and
///     prefixes with <c>_</c> if the result would otherwise start with a
///     non-letter (a digit, or the trailing-predicate marker <c>?</c>).
///     Returns null when the input has no usable identifier characters at
///     all, so the caller can fall through to the next naming level.
/// </summary>
public static class LetNameSanitizer
{
    /// <summary>
    ///     Identifier characters: ASCII letters, digits, underscore, and the
    ///     trailing-predicate marker <c>?</c> (Excel allows it anywhere
    ///     except the first character of a defined name; see
    ///     <see cref="ExcelNameValidator" />). Treating <c>?</c> as part of
    ///     the word means a label like <c>Help?</c> sanitises to <c>help?</c>
    ///     rather than silently stripping the marker.
    /// </summary>
    private static bool IsIdentifierChar(char c)
    {
        return c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '?';
    }

    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        // text is non-null after the IsNullOrEmpty guard; net48's BCL
        // doesn't annotate IsNullOrEmpty with [NotNullWhen(false)], so
        // the analyzer can't narrow on its own.
        var trimmed = text!.Trim();
        if (trimmed.Length == 0)
            return null;

        var words = SplitWords(trimmed);
        if (words.Count == 0)
            return null;

        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (i == 0)
            {
                // First word: lowercase the initial letter and keep the rest
                // as-is so already-camelCase labels survive intact.
                if (char.IsLetter(w[0]))
                    sb.Append(char.ToLowerInvariant(w[0]));
                else
                    sb.Append(w[0]);
                if (w.Length > 1)
                    sb.Append(w, 1, w.Length - 1);
            }
            else
            {
                // Subsequent words: TitleCase (uppercase initial letter,
                // lowercase the rest) so e.g. "Customer ID" → "customerId".
                if (char.IsLetter(w[0]))
                    sb.Append(char.ToUpperInvariant(w[0]));
                else
                    sb.Append(w[0]);
                if (w.Length > 1)
                    sb.Append(w[1..].ToLowerInvariant());
            }
        }

        if (sb.Length == 0)
            return null;

        // A valid Excel name starts with a letter or underscore; if the
        // first sanitised character is anything else (a digit, or the
        // trailing-predicate marker '?' that we now allow inside words),
        // prefix with '_' so the result is still a usable name. Cases:
        // "30" → "_30"; "?help" → "_?help".
        if (sb[0] != '_' && !char.IsLetter(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !IsIdentifierChar(text[i]))
                i++;
            var start = i;
            while (i < text.Length && IsIdentifierChar(text[i]))
                i++;
            if (i > start)
                words.Add(text[start..i]);
        }
        return words;
    }
}
