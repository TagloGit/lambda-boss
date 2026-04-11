using Xunit;

namespace LambdaBoss.Tests;

public class LambdaLoaderTests
{
    [Fact]
    public void GetTracerBulletLambdas_ReturnsThreeItems()
    {
        var lambdas = LambdaLoader.GetTracerBulletLambdas();
        Assert.Equal(3, lambdas.Count);
    }

    [Fact]
    public void GetTracerBulletLambdas_ContainsDouble()
    {
        var lambdas = LambdaLoader.GetTracerBulletLambdas();
        Assert.Contains(lambdas, l => l.Name == "DOUBLE" && l.Formula == "=LAMBDA(x, x*2)");
    }

    [Fact]
    public void GetTracerBulletLambdas_ContainsTriple()
    {
        var lambdas = LambdaLoader.GetTracerBulletLambdas();
        Assert.Contains(lambdas, l => l.Name == "TRIPLE" && l.Formula == "=LAMBDA(x, x*3)");
    }

    [Fact]
    public void GetTracerBulletLambdas_ContainsAddN()
    {
        var lambdas = LambdaLoader.GetTracerBulletLambdas();
        Assert.Contains(lambdas, l => l.Name == "ADDN" && l.Formula == "=LAMBDA(x, n, x+n)");
    }

    [Fact]
    public void GetTracerBulletLambdas_AllFormulasStartWithEquals()
    {
        var lambdas = LambdaLoader.GetTracerBulletLambdas();
        Assert.All(lambdas, l => Assert.StartsWith("=", l.Formula));
    }

    [Fact]
    public void GetTracerBulletLambdas_AllFormulasContainLambda()
    {
        var lambdas = LambdaLoader.GetTracerBulletLambdas();
        Assert.All(lambdas, l => Assert.Contains("LAMBDA", l.Formula));
    }

    [Fact]
    public void BuildComment_FormatsCorrectly()
    {
        var comment = LambdaLoader.BuildComment(
            "https://github.com/TagloGit/lambda-boss", "test", "tst");

        Assert.StartsWith(LambdaLoader.CommentMarker, comment);
        Assert.Contains("github.com/TagloGit/lambda-boss", comment);
        Assert.Contains("test", comment);
        Assert.Contains("tst", comment);
    }

    [Fact]
    public void BuildComment_TrimsTrailingSlash()
    {
        var comment = LambdaLoader.BuildComment(
            "https://github.com/TagloGit/lambda-boss/", "test", "tst");

        Assert.DoesNotContain("lambda-boss/|", comment);
    }

    [Fact]
    public void BuildComment_ContainsPipeDelimitedParts()
    {
        var comment = LambdaLoader.BuildComment(
            "https://github.com/Owner/repo", "mylib", "ml");

        // Should be: [LambdaBoss] https://github.com/Owner/repo|mylib|ml
        var afterMarker = comment[(LambdaLoader.CommentMarker.Length + 1)..];
        var parts = afterMarker.Split('|');
        Assert.Equal(3, parts.Length);
        Assert.Equal("https://github.com/Owner/repo", parts[0]);
        Assert.Equal("mylib", parts[1]);
        Assert.Equal("ml", parts[2]);
    }
}
