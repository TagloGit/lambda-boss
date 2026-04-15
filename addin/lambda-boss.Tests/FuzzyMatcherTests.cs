using Xunit;

namespace LambdaBoss.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void EmptyQuery_ReturnsZero()
    {
        Assert.Equal(0, FuzzyMatcher.Score("", "Anything"));
    }

    [Fact]
    public void NoSubsequence_ReturnsNoMatch()
    {
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("xyz", "ConsecGroups"));
    }

    [Fact]
    public void QueryLongerThanCandidate_ReturnsNoMatch()
    {
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("abcdef", "abc"));
    }

    [Fact]
    public void SubsequenceOutOfOrder_ReturnsNoMatch()
    {
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("ba", "abc"));
    }

    [Fact]
    public void CaseInsensitive_MatchesRegardlessOfCase()
    {
        Assert.NotEqual(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("CG", "consecgroups"));
        Assert.NotEqual(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("cg", "ConsecGroups"));
    }

    [Fact]
    public void CamelCaseInitialism_OutranksMidWordMatch()
    {
        var initialism = FuzzyMatcher.Score("cg", "ConsecGroups");
        var midWord = FuzzyMatcher.Score("cg", "Contiguous");
        Assert.True(initialism > midWord,
            $"CamelCase initialism score {initialism} should beat mid-word {midWord}");
    }

    [Fact]
    public void WordBoundaryAfterNonLetter_ScoresHigher()
    {
        var boundary = FuzzyMatcher.Score("r", "_range");
        var midWord = FuzzyMatcher.Score("r", "orange");
        Assert.True(boundary > midWord,
            $"Boundary score {boundary} should beat mid-word {midWord}");
    }

    [Fact]
    public void ConsecutiveMatches_BeatGapMatches()
    {
        var consecutive = FuzzyMatcher.Score("abc", "abcdef");
        var gapped = FuzzyMatcher.Score("abc", "axbxcx");
        Assert.True(consecutive > gapped);
    }
}
