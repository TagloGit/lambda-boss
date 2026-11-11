namespace LambdaBoss.UI;

/// <summary>
///     Finds the library whose name or default prefix the typed text matches exactly
///     (case-insensitive, surrounding spaces ignored). The popup's Enter key loads that
///     library ahead of the top search hit, so typing <c>maps</c> loads Maps (#449).
/// </summary>
internal static class ExactLibraryMatch
{
    public static LibraryDisplayItem? Find(IReadOnlyList<LibraryDisplayItem> libraries, string query)
    {
        var typed = query.Trim();
        if (typed.Length == 0)
            return null;

        return libraries.FirstOrDefault(l => string.Equals(l.DisplayName, typed, StringComparison.OrdinalIgnoreCase))
               ?? libraries.FirstOrDefault(l => string.Equals(l.DefaultPrefix, typed, StringComparison.OrdinalIgnoreCase));
    }
}
