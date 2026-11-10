using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     The library built into the add-in, used for the home repo when GitHub can't be reached
///     (a lone XLL on an offline competition machine).
/// </summary>
public class BuiltInLibraryTests : IDisposable
{
    private readonly string _tempCacheDir = Path.Combine(Path.GetTempPath(), $"lb-builtin-{Guid.NewGuid():N}");

    private static readonly RepoConfig HomeRepo = new() { Url = RepoConfig.HomeRepoUrl };
    private static readonly RepoConfig OtherRepo = new() { Url = "https://github.com/TestOwner/test-repo" };

    public void Dispose()
    {
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, recursive: true);
    }

    // No mock registered: every request answers 404, as an unreachable GitHub would fail.
    private LibraryProvider Offline(params RepoConfig[] repos) =>
        new(repos, new HttpClient(new MockHttpHandler()), new SourceCache(_tempCacheDir));

    [Fact]
    public void Default_CarriesTheRepoLibraries()
    {
        var builtIn = BuiltInLibrarySource.Default;

        Assert.Contains("maps", builtIn.ListLibraries());
        Assert.Contains("lookup", builtIn.ListLibraries());

        var maps = builtIn.FetchLibrary("maps")!;
        Assert.Contains("MAZE.lambda", maps.Files.Keys);
        Assert.Contains("_dirKnight.const", maps.Files.Keys);
        Assert.DoesNotContain(maps.Files.Keys, f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(maps.Metadata.Name));
    }

    [Fact]
    public void Default_MatchesTheLambdasFolder()
    {
        var lambdasDir = FindLambdasDir();
        var onDisk = Directory.GetDirectories(lambdasDir)
            .Where(d => File.Exists(Path.Combine(d, "_library.yaml")))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk, BuiltInLibrarySource.Default.ListLibraries());

        foreach (var lib in onDisk)
        {
            var expected = Directory.GetFiles(Path.Combine(lambdasDir, lib!))
                .Select(Path.GetFileName)
                .Where(f => f!.EndsWith(".lambda", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".const", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            var actual = BuiltInLibrarySource.Default.FetchLibrary(lib!)!.Files.Keys
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void FetchLibrary_UnknownLibrary_IsNull()
    {
        Assert.Null(BuiltInLibrarySource.Default.FetchLibrary("no-such-library"));
        Assert.False(BuiltInLibrarySource.Default.Has("no-such-library"));
    }

    [Fact]
    public async Task HomeRepo_Offline_LoadsTheBuiltInLibrary()
    {
        var lambdas = await Offline(HomeRepo).GetAllLambdasAsync();

        Assert.Contains(lambdas, l => l.Name == "TLOOKUP");
        Assert.Contains(lambdas, l => l.Name == "MAZE");
        Assert.Contains(lambdas, l => l.Name == "RELPOS");
        Assert.Contains(lambdas, l => l.Name == "_dirKnight" && l.IsConstant);
        Assert.NotNull(lambdas.First(l => l.Name == "MAZE").Doc);
        Assert.Equal("TagloGit/lambda-boss", lambdas.First(l => l.Name == "MAZE").LibraryInfo.RepoLabel);
    }

    [Fact]
    public async Task HomeRepo_Offline_LoadLibraryAsync_UsesTheBuiltInCopy()
    {
        var loaded = await Offline(HomeRepo).LoadLibraryAsync(HomeRepo, "maps", "");

        Assert.Contains(loaded, l => l.Name == "MAZE");
    }

    [Fact]
    public async Task OtherRepo_Offline_LoadsNothing()
    {
        var lambdas = await Offline(OtherRepo).GetAllLambdasAsync();

        Assert.Empty(lambdas);
    }

    [Fact]
    public async Task OtherRepo_Offline_LoadLibraryAsync_StillFails()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Offline(OtherRepo).LoadLibraryAsync(OtherRepo, "maps", ""));
    }

    [Fact]
    public async Task HomeRepo_Online_ReadsGitHubNotTheBuiltInCopy()
    {
        var handler = new MockHttpHandler();
        handler.Register("contents/lambdas/only?ref=main", "[{\"name\":\"Only.lambda\",\"type\":\"file\"}]");
        handler.Register("contents/lambdas?ref=main", "[{\"name\":\"only\",\"type\":\"dir\"}]");
        handler.Register("TagloGit/lambda-boss/main/lambdas/only/_library.yaml",
            "name: Only\ndescription: d\ndefault_prefix: o");
        handler.Register("TagloGit/lambda-boss/main/lambdas/only/Only.lambda", "Only = LAMBDA(x, x);");
        var provider = new LibraryProvider(new[] { HomeRepo }, new HttpClient(handler), new SourceCache(_tempCacheDir));

        var libraries = await provider.GetLibrariesAsync();

        Assert.Equal("only", Assert.Single(libraries).FolderName);
    }

    [Fact]
    public void DefaultClient_GivesUpWithinSeconds()
    {
        Assert.True(LibraryProvider.RequestTimeout <= TimeSpan.FromSeconds(15));
    }

    private static string FindLambdasDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "lambdas")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "lambdas");
    }
}
