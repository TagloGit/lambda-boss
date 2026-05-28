using System.Text.RegularExpressions;

namespace LambdaBoss;

/// <summary>
///     Syntactic validation of workbook-scoped defined names. Does not check
///     for collisions — that requires the live Name Manager.
/// </summary>
public static class ExcelNameValidator
{
    private static readonly Regex CellRefPattern = new(
        @"^[A-Za-z]{1,3}[0-9]+$|^R[0-9]+C[0-9]+$",
        RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "C",
        "c",
        "R",
        "r",
        "TRUE",
        "FALSE",
    };

    public static ValidationResult Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ValidationResult.Invalid("Name is required.");

        // name is non-null after the IsNullOrWhiteSpace guard; net48's
        // BCL doesn't annotate IsNullOrWhiteSpace with [NotNullWhen(false)],
        // so the analyzer can't narrow on its own. Re-bind to a non-null
        // local so the rest of the method reads cleanly.
        var n = name!;

        if (n.Length > 255)
            return ValidationResult.Invalid("Name is too long (max 255 characters).");

        var first = n[0];
        if (!char.IsLetter(first) && first != '_' && first != '\\')
            return ValidationResult.Invalid("Name must start with a letter, underscore, or backslash.");

        foreach (var c in n)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '\\' && c != '?')
                return ValidationResult.Invalid($"Invalid character '{c}' in name.");

        if (Reserved.Contains(n))
            return ValidationResult.Invalid($"'{n}' is reserved by Excel.");

        if (CellRefPattern.IsMatch(n))
            return ValidationResult.Invalid($"'{n}' looks like a cell reference.");

        return ValidationResult.Valid();
    }
}

public record ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Valid()
    {
        return new ValidationResult(true, null);
    }

    public static ValidationResult Invalid(string error)
    {
        return new ValidationResult(false, error);
    }
}