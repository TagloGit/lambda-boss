namespace LambdaBoss;

/// <summary>
///     Per-row canonicality check for user-typed binding names in the
///     Gather dialog. A name is <em>canonical</em> when it round-trips
///     through <see cref="LetNameSanitizer" /> unchanged AND
///     <see cref="ExcelNameValidator" /> accepts it. The sanitizer
///     check catches names that would be silently transformed (so what
///     the user types matches what lands in the LET), and the
///     Excel-name check rules out reserved names (<c>TRUE</c>,
///     <c>FALSE</c>, <c>R</c>, <c>C</c>) and cell-ref-shaped names
///     (<c>a1</c>) — both would survive the sanitiser but produce a
///     LET that Excel refuses on write.
///
///     Collisions between rows are <em>not</em> handled here. The
///     engine resolves user-override collisions by suffixing
///     (<c>x</c> → <c>x_2</c>); the dialog reflects the engine's
///     resolved name back into the row's TextBox. Treating collisions
///     as invalid would force the user to pre-resolve a name conflict
///     the engine can fix automatically — and would disable Save on
///     LETs that are perfectly well-formed.
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
}
