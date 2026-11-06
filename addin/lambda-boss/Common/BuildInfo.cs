using System.Reflection;

namespace LambdaBoss.Common;

/// <summary>
///     What this build is, from the assembly's informational version ("0.2.0-dev.57+&lt;commit&gt;").
///     CI stamps it; a local build gets the SDK's default.
/// </summary>
internal static class BuildInfo
{
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static string? Commit => ParseCommit(Informational);

    public static string Display => ParseDisplay(Informational);

    public static string? ParseCommit(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0 || plus == informational.Length - 1)
        {
            return null;
        }

        return informational.Substring(plus + 1);
    }

    public static string ParseDisplay(string informational)
    {
        var commit = ParseCommit(informational);
        if (commit == null)
        {
            return informational.TrimEnd('+');
        }

        var version = informational.Substring(0, informational.IndexOf('+'));
        var shortCommit = commit.Length > 7 ? commit.Substring(0, 7) : commit;
        return $"{version} ({shortCommit})";
    }
}
