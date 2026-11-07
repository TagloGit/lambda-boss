using Xunit;

namespace LambdaBoss.Tests;

public class ExcelNameValidatorTests
{
    [Theory]
    [InlineData("MyLambda")]
    [InlineData("_underscore")]
    [InlineData("Name123")]
    [InlineData("dotted.name")]
    public void ValidNames(string name)
    {
        Assert.True(ExcelNameValidator.Validate(name).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1StartsWithDigit")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("A1")]
    [InlineData("ZZ999")]
    [InlineData("R1C1")]
    [InlineData("TRUE")]
    [InlineData("false")]
    [InlineData("C")]
    public void InvalidNames(string? name)
    {
        var result = ExcelNameValidator.Validate(name);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }
}
