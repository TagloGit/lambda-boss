using Xunit;

#pragma warning disable CA1707

namespace LambdaBoss.Tests;

public class LocalDirectorySourceTests : IDisposable
{
    private readonly string _tempDir;

    public LocalDirectorySourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LambdaBoss_LocalSrc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void ListLibraries_ReturnsDirectoriesWithLibraryYaml()
    {
        // Arrange: two dirs, only one has _library.yaml
        var lib1 = Path.Combine(_tempDir, "string");
        var lib2 = Path.Combine(_tempDir, "math");
        var notLib = Path.Combine(_tempDir, "random");

        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);
        Directory.CreateDirectory(notLib);

        File.WriteAllText(Path.Combine(lib1, "_library.yaml"), "name: String\ndescription: String ops\ndefault_prefix: str");
        File.WriteAllText(Path.Combine(lib2, "_library.yaml"), "name: Math\ndescription: Math ops\ndefault_prefix: math");
        // notLib has no _library.yaml

        var config = new LocalSourceConfig { Path = _tempDir };
        var source = new LocalDirectorySource(config);

        // Act
        var libraries = source.ListLibraries();

        // Assert
        Assert.Equal(2, libraries.Count);
        Assert.Contains("string", libraries);
        Assert.Contains("math", libraries);
        Assert.DoesNotContain("random", libraries);
    }

    [Fact]
    public void ListLibraries_WhenPathDoesNotExist_ReturnsEmpty()
    {
        var config = new LocalSourceConfig { Path = @"C:\nonexistent\path\12345" };
        var source = new LocalDirectorySource(config);

        var libraries = source.ListLibraries();

        Assert.Empty(libraries);
    }

    [Fact]
    public void FetchLibrary_ReadsMetadataAndLambdaFiles()
    {
        // Arrange
        var libDir = Path.Combine(_tempDir, "string");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(libDir, "_library.yaml"),
            "name: String Functions\ndescription: String ops\ndefault_prefix: str");
        File.WriteAllText(Path.Combine(libDir, "Split.lambda"),
            "Split = LAMBDA([text], [delimiter], TEXTSPLIT(text, delimiter));");
        File.WriteAllText(Path.Combine(libDir, "Join.lambda"),
            "Join = LAMBDA([parts], [delimiter], TEXTJOIN(delimiter, TRUE, parts));");

        var config = new LocalSourceConfig { Path = _tempDir };
        var source = new LocalDirectorySource(config);

        // Act
        var library = source.FetchLibrary("string");

        // Assert
        Assert.Equal("String Functions", library.Metadata.Name);
        Assert.Equal("String ops", library.Metadata.Description);
        Assert.Equal("str", library.Metadata.DefaultPrefix);
        Assert.Equal(2, library.Files.Count);
        Assert.True(library.Files.ContainsKey("Split.lambda"));
        Assert.True(library.Files.ContainsKey("Join.lambda"));
    }

    [Fact]
    public void FetchLibrary_ReadsOnlyLambdaFiles()
    {
        // Arrange
        var libDir = Path.Combine(_tempDir, "test");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(libDir, "_library.yaml"),
            "name: Test\ndescription: Test lib\ndefault_prefix: tst");
        File.WriteAllText(Path.Combine(libDir, "Func.lambda"), "Func = LAMBDA(x, x);");
        File.WriteAllText(Path.Combine(libDir, "notes.txt"), "Some notes");
        File.WriteAllText(Path.Combine(libDir, "Func.tests.yaml"), "tests: []");

        var config = new LocalSourceConfig { Path = _tempDir };
        var source = new LocalDirectorySource(config);

        // Act
        var library = source.FetchLibrary("test");

        // Assert
        Assert.Single(library.Files);
        Assert.True(library.Files.ContainsKey("Func.lambda"));
    }
}
