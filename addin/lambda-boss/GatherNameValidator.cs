namespace LambdaBoss;

/// <summary>
///     Validates user-typed binding names in the Gather dialog. A name is
///     <em>canonical</em> when it round-trips through
///     <see cref="LetNameSanitizer" /> unchanged AND
///     <see cref="ExcelNameValidator" /> accepts it. The sanitizer check
///     catches names that would be silently transformed (so what the
///     user types matches what lands in the LET), and the Excel-name
///     check rules out reserved names (<c>TRUE</c>, <c>FALSE</c>,
///     <c>R</c>, <c>C</c>) and cell-ref-shaped names (<c>a1</c>) — both
///     would survive the sanitiser but produce a LET that Excel
///     refuses on write.
/// </summary>
internal static class GatherNameValidator
{
    public static bool IsCanonical(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (LetNameSanitizer.Sanitize(name) != name)
            return false;
        return ExcelNameValidator.Validate(name).IsValid;
    }

    /// <summary>
    ///     Validates a list of names as a group. Each name is independently
    ///     canonical-checked; any name that appears more than once in the
    ///     list (case-insensitive — Excel's name resolution rule) is
    ///     marked invalid for every occurrence so the user sees both
    ///     colliding rows light up. Returns an array of the same length
    ///     as <paramref name="names" />, with <c>true</c> for valid
    ///     entries and <c>false</c> for invalid ones.
    /// </summary>
    public static bool[] Validate(IReadOnlyList<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var result = new bool[names.Count];
        var counts = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n)) continue;
            counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
        }

        for (var i = 0; i < names.Count; i++)
        {
            var n = names[i];
            if (!IsCanonical(n))
            {
                result[i] = false;
                continue;
            }
            result[i] = counts[n!] == 1;
        }

        return result;
    }
}
