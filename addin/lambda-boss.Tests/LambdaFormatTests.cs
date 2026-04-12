using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Validates that all .lambda files conform to the required format.
///     Runs in CI — no Excel required.
/// </summary>
public class LambdaFormatTests
{
    private static readonly string LambdasRoot = FindLambdasRoot();

    private static string FindLambdasRoot()
    {
        // Walk up from the test assembly's output directory to find the repo root
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

    public static TheoryData<string> LambdaFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(LambdasRoot, "*.lambda", SearchOption.AllDirectories))
            // Store path relative to the lambdas root for readable test names
            data.Add(Path.GetRelativePath(LambdasRoot, file));
        return data;
    }

    private string ReadFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(LambdasRoot, relativePath));
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void FilenameMatchesLambdaName(string relativePath)
    {
        var expectedName = Path.GetFileNameWithoutExtension(relativePath);
        var content = ReadFile(relativePath);

        // First non-comment line should be "Name = LAMBDA("
        var nameMatch = GetNameAssignment(content);
        Assert.True(nameMatch != null,
            $"Could not find name assignment in {relativePath}");
        Assert.Equal(expectedName, nameMatch);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void ContainsHeaderComment(string relativePath)
    {
        var content = ReadFile(relativePath);

        Assert.Contains("FUNCTION NAME:", content);
        Assert.Contains("DESCRIPTION:", content);
        Assert.Contains("REVISIONS:", content);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void ContainsHelpTextsplitPattern(string relativePath)
    {
        var content = ReadFile(relativePath);

        // Must contain the TEXTSPLIT with → and ¶ delimiters
        Assert.Matches(@"TEXTSPLIT\(", content);
        Assert.Contains("\"→\"", content);
        Assert.Contains("\"¶\"", content);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void HelpIsLetVariable(string relativePath)
    {
        var content = ReadFile(relativePath);

        // Help? must be defined as a LET variable (e.g. "Help?, ISOMITTED(")
        Assert.Matches(@"Help\?,\s*ISOMITTED\(", content);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void NoTabCharacters(string relativePath)
    {
        var content = ReadFile(relativePath);

        Assert.DoesNotContain("\t", content);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void NoCarriageReturnCharacters(string relativePath)
    {
        // Read raw bytes to detect \r before any normalisation
        var bytes = File.ReadAllBytes(Path.Combine(LambdasRoot, relativePath));
        var raw = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\r", raw);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void EndsWithSemicolon(string relativePath)
    {
        var content = ReadFile(relativePath).TrimEnd();

        Assert.EndsWith(");", content);
    }

    [Theory]
    [MemberData(nameof(LambdaFiles))]
    public void NameAssignmentIsFirstNonCommentLine(string relativePath)
    {
        var content = ReadFile(relativePath);
        var expectedName = Path.GetFileNameWithoutExtension(relativePath);

        var firstNonComment = GetFirstNonCommentLine(content);
        Assert.True(firstNonComment != null,
            $"No non-comment lines found in {relativePath}");

        var pattern = $@"^{Regex.Escape(expectedName)}\s*=\s*LAMBDA\(";
        Assert.Matches(pattern, firstNonComment);
    }

    /// <summary>
    ///     Extracts the lambda name from the first "Name = LAMBDA(" assignment line.
    /// </summary>
    private static string? GetNameAssignment(string content)
    {
        var match = Regex.Match(content, @"^(\w+)\s*=\s*LAMBDA\(", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    ///     Returns the first line that isn't a block comment or inside a block comment.
    /// </summary>
    private static string? GetFirstNonCommentLine(string content)
    {
        var lines = content.Split('\n');
        var inBlock = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Track block comment state
            if (inBlock)
            {
                if (line.Contains("*/"))
                    inBlock = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Line starts a block comment
            if (line.TrimStart().StartsWith("/*"))
            {
                if (!line.Contains("*/"))
                    inBlock = true;
                else if (line.LastIndexOf("*/", StringComparison.Ordinal) < line.Length - 2)
                {
                    // There's content after the closing */ on the same line —
                    // but in the header format this is another comment line
                    // Check if there's meaningful non-comment content after last */
                }

                continue;
            }

            // Skip single-line comments
            if (line.TrimStart().StartsWith("//"))
                continue;

            return line.Trim();
        }

        return null;
    }
}