namespace LambdaBoss;

/// <summary>
///     Subsequence fuzzy matcher with word-boundary ranking. Query characters
///     must appear in order (not necessarily contiguous) within the candidate.
///     Matches on word boundaries (start of string, after non-letter/digit, or
///     a capital letter following a lowercase letter) score higher, which
///     surfaces initialism-style matches (e.g. "cg" → "ConsecGroups").
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = -1;

    private const int BaseScore = 1;
    private const int BoundaryBonus = 10;
    private const int ConsecutiveBonus = 5;

    /// <summary>
    ///     Returns a match score for the query against the candidate, or
    ///     <see cref="NoMatch"/> if the query is not a subsequence of the
    ///     candidate. Matching is case-insensitive. Higher is better.
    /// </summary>
    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query))
            return 0;
        if (string.IsNullOrEmpty(candidate))
            return NoMatch;

        var score = 0;
        var candidateIndex = 0;
        var lastMatchIndex = -2;

        foreach (var qc in query)
        {
            var target = char.ToLowerInvariant(qc);

            while (candidateIndex < candidate.Length
                   && char.ToLowerInvariant(candidate[candidateIndex]) != target)
                candidateIndex++;

            if (candidateIndex >= candidate.Length)
                return NoMatch;

            score += BaseScore;
            if (IsWordBoundary(candidate, candidateIndex))
                score += BoundaryBonus;
            if (candidateIndex == lastMatchIndex + 1)
                score += ConsecutiveBonus;

            lastMatchIndex = candidateIndex;
            candidateIndex++;
        }

        return score;
    }

    private static bool IsWordBoundary(string s, int index)
    {
        if (index == 0)
            return true;
        var prev = s[index - 1];
        var curr = s[index];
        if (!char.IsLetterOrDigit(prev))
            return true;
        // CamelCase boundary: lowercase → uppercase
        if (char.IsUpper(curr) && char.IsLower(prev))
            return true;
        return false;
    }
}
