using Xunit;

namespace LambdaBoss.Tests;

public class LibraryMetadataTests
{
    [Fact]
    public void LoadFromString_ValidYaml_DeserializesCorrectly()
    {
        var yaml = @"name: Test
description: Test library for development
default_prefix: tst";

        var metadata = LibraryMetadata.LoadFromString(yaml);

        Assert.Equal("Test", metadata.Name);
        Assert.Equal("Test library for development", metadata.Description);
        Assert.Equal("tst", metadata.DefaultPrefix);
    }

    [Fact]
    public void LoadFromString_EmptyPrefix_DeserializesAsEmpty()
    {
        var yaml = @"name: NoPrefix
description: A library without prefix
default_prefix: """"";

        var metadata = LibraryMetadata.LoadFromString(yaml);

        Assert.Equal("NoPrefix", metadata.Name);
        Assert.Empty(metadata.DefaultPrefix);
    }

    [Fact]
    public void LoadFromString_MissingFields_DefaultsToEmpty()
    {
        var yaml = "name: Minimal";

        var metadata = LibraryMetadata.LoadFromString(yaml);

        Assert.Equal("Minimal", metadata.Name);
        Assert.Equal("", metadata.Description);
        Assert.Equal("", metadata.DefaultPrefix);
    }
}
