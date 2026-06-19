using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Validates that all .const files conform to the constant format.
///     Runs in CI — no Excel required. Implemented as a single fact looping over
///     every .const file so it passes cleanly when none exist yet (constants are
///     migrated from .lambda as a follow-up) and gains teeth the moment they ship.
/// </summary>
public class ConstFormatTests
{
    private static readonly string LambdasRoot = FindLambdasRoot();

    private static string FindLambdasRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "lambdas");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find 'lambdas' directory from " +
                                             Directory.GetCurrentDirectory());
    }

    [Fact]
    public void AllConstFiles_ConformToFormat()
    {
        var files = Directory.EnumerateFiles(LambdasRoot, "*.const", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var expectedName = Path.GetFileNameWithoutExtension(file);

            // Header convention.
            Assert.True(content.Contains("CONSTANT NAME:"),
                $"{file}: missing 'CONSTANT NAME:' header.");
            Assert.True(content.Contains("DESCRIPTION:"),
                $"{file}: missing 'DESCRIPTION:' header.");
            Assert.True(content.Contains("REVISIONS:"),
                $"{file}: missing 'REVISIONS:' header.");

            // No tabs / carriage returns (raw bytes for the CR check).
            Assert.False(content.Contains("\t"), $"{file}: contains tab characters.");
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.False(raw.Contains("\r"), $"{file}: contains carriage-return characters.");

            // Statement terminator.
            Assert.True(content.TrimEnd().EndsWith(";"), $"{file}: must end with ';'.");

            // Parses, filename matches the constant name, and the RHS is not a LAMBDA.
            var (name, formula) = ConstParser.Parse(content);
            Assert.True(string.Equals(expectedName, name, StringComparison.Ordinal),
                $"{file}: filename '{expectedName}' does not match constant name '{name}'.");
            Assert.False(Regex.IsMatch(formula, @"^=\s*LAMBDA\s*\(", RegexOptions.IgnoreCase),
                $"{file}: RHS must not be a LAMBDA — use a .lambda file instead.");
        }
    }
}
