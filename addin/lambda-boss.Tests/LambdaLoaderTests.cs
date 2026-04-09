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
}
