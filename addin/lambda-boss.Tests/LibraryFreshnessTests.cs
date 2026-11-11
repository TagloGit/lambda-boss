using System.Net;
using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     #452: a load gives the current library when GitHub answers; offline, the newer of the disk
///     cache and the built-in copy; and the popup can tell a workbook's copy is out of date.
/// </summary>
public class LibraryFreshnessTests : IDisposable
{
    private readonly string _tempCacheDir = Path.Combine(Path.GetTempPath(), $"lb-fresh-{Guid.NewGuid():N}");

    private static readonly RepoConfig HomeRepo = new() { Url = RepoConfig.HomeRepoUrl };

    public void Dispose()
    {
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, recursive: true);
    }

    private static MockHttpHandler GitHubWith(string lambda)
    {
        var handler = new MockHttpHandler();
        handler.Register("contents/lambdas/only?ref=main", "[{\"name\":\"Only.lambda\",\"type\":\"file\"}]");
        handler.Register("contents/lambdas?ref=main", "[{\"name\":\"only\",\"type\":\"dir\"}]");
        handler.Register("TagloGit/lambda-boss/main/lambdas/only/_library.yaml",
            "name: Only\ndescription: d\ndefault_prefix: o");
        handler.Register("TagloGit/lambda-boss/main/lambdas/only/Only.lambda", lambda);
        return handler;
    }

    private static void Seed(SourceCache cache, string lambda, DateTime storedAt, string dir)
    {
        cache.Store(HomeRepo, new FetchedLibrary("only",
            LibraryMetadata.LoadFromString("name: Only\ndescription: d\ndefault_prefix: o"),
            new Dictionary<string, string> { ["Only.lambda"] = lambda }));
        File.SetLastWriteTimeUtc(Path.Combine(dir, HomeRepo.GetCacheKey(), "only", "_library.yaml"), storedAt);
    }

    [Fact]
    public async Task Load_Online_IgnoresAFreshCache()
    {
        var cache = new SourceCache(_tempCacheDir, ttl: TimeSpan.FromMinutes(60));
        Seed(cache, "Only = LAMBDA(x, x + 1);", DateTime.UtcNow, _tempCacheDir);
        var provider = new LibraryProvider(new[] { HomeRepo },
            new HttpClient(GitHubWith("Only = LAMBDA(x, x + 2);")), cache);

        var loaded = await provider.LoadLibraryAsync(HomeRepo, "only", "");

        Assert.Contains("x + 2", Assert.Single(loaded).Formula);
        Assert.Null(provider.FallbackNote);
    }

    [Fact]
    public async Task Load_FetchFails_CacheNewerThanBuild_UsesCache()
    {
        var cache = new SourceCache(_tempCacheDir, ttl: TimeSpan.FromMinutes(60));
        Seed(cache, "ONLY = LAMBDA(x, x + 1);", DateTime.UtcNow - TimeSpan.FromDays(1), _tempCacheDir);
        var builtIn = new BuiltInLibrarySource(typeof(LibraryFreshnessTests).Assembly, DateTime.UtcNow - TimeSpan.FromDays(2));
        var provider = new LibraryProvider(new[] { HomeRepo }, new HttpClient(new MockHttpHandler()), cache, builtIn: builtIn);

        var loaded = await provider.LoadLibraryAsync(HomeRepo, "only", "");

        Assert.Contains("x + 1", Assert.Single(loaded).Formula);
        Assert.Contains("saved", provider.FallbackNote);
    }

    [Fact]
    public async Task Load_FetchFails_CacheOlderThanBuild_UsesBuiltIn()
    {
        var cache = new SourceCache(_tempCacheDir, ttl: TimeSpan.FromMinutes(60));
        var builtIn = new BuiltInLibrarySource(typeof(LibraryProvider).Assembly, DateTime.UtcNow - TimeSpan.FromDays(1));
        var provider = new LibraryProvider(new[] { HomeRepo }, new HttpClient(new MockHttpHandler()), cache, builtIn: builtIn);

        // A cached Maps from three days ago against an add-in built yesterday.
        cache.Store(HomeRepo, new FetchedLibrary("maps",
            LibraryMetadata.LoadFromString("name: Maps\ndescription: d\ndefault_prefix: "),
            new Dictionary<string, string> { ["MAZE.lambda"] = "MAZE = LAMBDA(x, \"stale\");" }));
        File.SetLastWriteTimeUtc(Path.Combine(_tempCacheDir, HomeRepo.GetCacheKey(), "maps", "_library.yaml"),
            DateTime.UtcNow - TimeSpan.FromDays(3));

        var loaded = await provider.LoadLibraryAsync(HomeRepo, "maps", "");

        Assert.DoesNotContain(loaded, l => l.Formula.Contains("stale"));
        Assert.Contains(loaded, l => l.Name == "MAZE");
        Assert.Contains("built into the add-in", provider.FallbackNote);
    }

    [Fact]
    public async Task Load_RateLimited_SaysSo()
    {
        var handler = new MockHttpHandler();
        handler.Register("lambda-boss/main/lambdas/maps/_library.yaml", "name: Maps\ndescription: d\ndefault_prefix: ");
        handler.Register("api.github.com", "{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden,
            new Dictionary<string, string> { ["X-RateLimit-Remaining"] = "0", ["X-RateLimit-Reset"] = "1794412227" });
        var provider = new LibraryProvider(new[] { HomeRepo }, new HttpClient(handler), new SourceCache(_tempCacheDir));

        var loaded = await provider.LoadLibraryAsync(HomeRepo, "maps", "");

        Assert.Contains(loaded, l => l.Name == "MAZE");
        Assert.Contains("hourly limit", provider.FallbackNote);
    }

    [Fact]
    public void BuiltIn_Default_CarriesItsBuildTime()
    {
        Assert.NotNull(BuiltInLibrarySource.Default.BuiltAtUtc);
        Assert.True(BuiltInLibrarySource.Default.BuiltAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void Staleness_SameDefinitions_IsCurrent()
    {
        var workbook = new Dictionary<string, string> { ["SetVars"] = "=LAMBDA(a,  b, a+b)" };
        var current = new List<(string, string)> { ("SetVars", "=LAMBDA(a, b, a + b)") };

        Assert.Equal((0, 1), LibraryStaleness.Compare(workbook, current));
    }

    [Fact]
    public void Staleness_ChangedOrMissingName_Counts()
    {
        var workbook = new Dictionary<string, string>
        {
            ["SetVars"] = "=LAMBDA(a, b, a+b)",
            ["Sheet1!GetVar"] = "=LAMBDA(s, s)",
            ["Retired"] = "=LAMBDA(x, x)"
        };
        var current = new List<(string, string)>
        {
            ("SetVars", "=LAMBDA(a, b, IFERROR(a+b, a))"),
            ("GetVar", "=LAMBDA(s, s)"),
            ("SplitState", "=LAMBDA(s, s)")
        };

        Assert.Equal((2, 3), LibraryStaleness.Compare(workbook, current));
    }

    [Fact]
    public void Staleness_ExcelsSheetQualifiedRef_IsTheSame()
    {
        // Excel stores #REF! in a name as "<active sheet>!#REF!" (VM, 11 Nov: GetVar, SplitState).
        Assert.Equal(LibraryStaleness.Normalize("=CHOOSE(i, #VALUE!, #REF!, #N/A)"),
            LibraryStaleness.Normalize("=CHOOSE(i,#VALUE!,Case!#REF!,#N/A)"));
        Assert.Equal(LibraryStaleness.Normalize("=IF(x, #REF!)"),
            LibraryStaleness.Normalize("=IF(x, 'My Sheet'!#REF!)"));
    }

    [Fact]
    public void Staleness_TextInsideStrings_StillMatters()
    {
        Assert.NotEqual(LibraryStaleness.Normalize("=\"a b\""), LibraryStaleness.Normalize("=\"ab\""));
        Assert.NotEqual(LibraryStaleness.Normalize("=\"A\""), LibraryStaleness.Normalize("=\"a\""));
        Assert.Equal(LibraryStaleness.Normalize("=let(x, 1, x)"), LibraryStaleness.Normalize("LET(x,1,x)"));
    }
}
