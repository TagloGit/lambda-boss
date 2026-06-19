using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     End-to-end loader coverage for .const files: discovery, parsing, prefixing,
///     and cross-references from a LAMBDA to a constant in the same library.
/// </summary>
public class ConstLoaderIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libDir;

    public ConstLoaderIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_Const_" + Guid.NewGuid().ToString("N")[..8]);
        _libDir = Path.Combine(_tempDir, "maps");
        Directory.CreateDirectory(_libDir);
        File.WriteAllText(Path.Combine(_libDir, "_library.yaml"),
            "name: Maps\ndescription: Map helpers\ndefault_prefix: maps");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void LoadLibrary_ConstFile_LoadsWithPrefixedName()
    {
        File.WriteAllText(Path.Combine(_libDir, "DEFAULTARROWS.const"),
            "DEFAULTARROWS = {\"↑\";\"↓\";\"←\";\"→\"};");

        var loaded = LambdaLoader.LoadLibrary(_libDir);

        var entry = Assert.Single(loaded);
        Assert.Equal("maps.DEFAULTARROWS", entry.Name);
        Assert.Equal("={\"↑\";\"↓\";\"←\";\"→\"}", entry.Formula);
    }

    [Fact]
    public void LoadLibrary_LambdaReferencingConstant_PrefixesTheConstant()
    {
        File.WriteAllText(Path.Combine(_libDir, "DEFAULTARROWS.const"),
            "DEFAULTARROWS = {\"↑\";\"↓\";\"←\";\"→\"};");
        File.WriteAllText(Path.Combine(_libDir, "FIRSTARROW.lambda"),
            "FIRSTARROW = LAMBDA(INDEX(DEFAULTARROWS, 1));");

        var loaded = LambdaLoader.LoadLibrary(_libDir);

        var lambda = loaded.Single(l => l.Name == "maps.FIRSTARROW");
        Assert.Contains("maps.DEFAULTARROWS", lambda.Formula);
        // The constant reference carries no parens but is still prefixed.
        Assert.DoesNotContain("INDEX(DEFAULTARROWS", lambda.Formula);
    }

    [Fact]
    public void LoadLibrary_NoPrefix_LeavesConstantBare()
    {
        var npDir = Path.Combine(_tempDir, "noprefix");
        Directory.CreateDirectory(npDir);
        File.WriteAllText(Path.Combine(npDir, "_library.yaml"),
            "name: NP\ndescription: no prefix\ndefault_prefix:");
        File.WriteAllText(Path.Combine(npDir, "ANSWER.const"), "ANSWER = 42;");

        var loaded = LambdaLoader.LoadLibrary(npDir);

        var entry = Assert.Single(loaded);
        Assert.Equal("ANSWER", entry.Name);
        Assert.Equal("=42", entry.Formula);
    }

    [Fact]
    public void LocalDirectorySource_FetchLibrary_IncludesConstFiles()
    {
        File.WriteAllText(Path.Combine(_libDir, "Helper.lambda"), "Helper = LAMBDA(x, x);");
        File.WriteAllText(Path.Combine(_libDir, "DEFAULTARROWS.const"),
            "DEFAULTARROWS = {\"↑\";\"↓\"};");

        var source = new LocalDirectorySource(new LocalSourceConfig { Path = _tempDir });
        var library = source.FetchLibrary("maps");

        Assert.Equal(2, library.Files.Count);
        Assert.True(library.Files.ContainsKey("Helper.lambda"));
        Assert.True(library.Files.ContainsKey("DEFAULTARROWS.const"));
    }

    [Fact]
    public void LoadWithPrefix_MixedLibrary_PrefixesLambdasAndConstants()
    {
        File.WriteAllText(Path.Combine(_libDir, "DEFAULTARROWS.const"),
            "DEFAULTARROWS = {\"↑\";\"↓\"};");
        File.WriteAllText(Path.Combine(_libDir, "FIRSTARROW.lambda"),
            "FIRSTARROW = LAMBDA(INDEX(DEFAULTARROWS, 1));");

        var source = new LocalDirectorySource(new LocalSourceConfig { Path = _tempDir });
        var loaded = source.FetchLibrary("maps").LoadWithPrefix();

        Assert.Contains(loaded, l => l.Name == "maps.DEFAULTARROWS");
        var lambda = loaded.Single(l => l.Name == "maps.FIRSTARROW");
        Assert.Contains("maps.DEFAULTARROWS", lambda.Formula);
    }
}
