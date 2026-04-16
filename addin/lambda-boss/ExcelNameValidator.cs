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

        if (name.Length > 255)
            return ValidationResult.Invalid("Name is too long (max 255 characters).");

        var first = name[0];
        if (!char.IsLetter(first) && first != '_' && first != '\\')
            return ValidationResult.Invalid("Name must start with a letter, underscore, or backslash.");

        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '\\' && c != '?')
                return ValidationResult.Invalid($"Invalid character '{c}' in name.");

        if (Reserved.Contains(name))
            return ValidationResult.Invalid($"'{name}' is reserved by Excel.");

        if (CellRefPattern.IsMatch(name))
            return ValidationResult.Invalid($"'{name}' looks like a cell reference.");

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