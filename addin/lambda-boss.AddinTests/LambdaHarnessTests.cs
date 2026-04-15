using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LambdaBoss.AddinTests;

[Collection("Excel Addin")]
public class LambdaHarnessTests
{
    private static readonly HashSet<string> InjectedNames = [];
    private static readonly object InjectionLock = new();

    private readonly ExcelAddinFixture _excel;
    private readonly ITestOutputHelper _output;

    public LambdaHarnessTests(ExcelAddinFixture excel, ITestOutputHelper output)
    {
        _excel = excel;
        _output = output;
    }

    public static IEnumerable<object[]> TestCases()
    {
        var lambdasDir = FindLambdasDirectory();
        var yamlFiles = Directory.GetFiles(lambdasDir, "*.tests.yaml", SearchOption.AllDirectories);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        foreach (var yamlPath in yamlFiles)
        {
            var lambdaFileName = Path.GetFileName(yamlPath).Replace(".tests.yaml", ".lambda");
            var lambdaPath = Path.Combine(Path.GetDirectoryName(yamlPath)!, lambdaFileName);

            if (!File.Exists(lambdaPath))
                continue;

            var yamlContent = File.ReadAllText(yamlPath);
            var suite = deserializer.Deserialize<TestSuite>(yamlContent);

            foreach (var test in suite.Tests)
                yield return
                [
                    lambdaPath,
                    test.Name,
                    test.Args ?? [],
                    test.Expected ?? "",
                    test.ExpectedType ?? ""
                ];
        }
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public void LambdaTest(string lambdaPath, string testName, List<object> args,
        object? expected, string expectedType)
    {
        var (name, formula) = LambdaParser.ParseFile(lambdaPath);
        EnsureInjected(name, formula);

        var argsStr = string.Join(",", args.Select(FormatArg));
        var cellFormula = $"={name}({argsStr})";
        _output.WriteLine($"[{testName}] {cellFormula}");

        var ws = _excel.AddWorksheet();
        try
        {
            var cell = ws.Range["A1"];
            try
            {
                cell.Formula2 = cellFormula;
                Thread.Sleep(500);

                if (!string.IsNullOrEmpty(expectedType))
                    AssertType(cell, expectedType, testName);
                else if (expected is List<object> expectedRows)
                    AssertArray(cell, expectedRows, testName);
                else
                    AssertScalar(cell, expected, testName);
            }
            finally
            {
                Marshal.ReleaseComObject(cell);
            }
        }
        finally
        {
            try
            {
                ws.Delete();
                Marshal.ReleaseComObject(ws);
            }
            catch
            {
                // Ignore cleanup
            }
        }
    }

    private void EnsureInjected(string name, string formula)
    {
        lock (InjectionLock)
        {
            if (InjectedNames.Count == 0)
                InjectAllLambdas();

            if (InjectedNames.Contains(name))
                return;

            // Fallback for any lambda not found during bulk injection
            _output.WriteLine($"Injecting {name}: {formula[..Math.Min(80, formula.Length)]}...");
            _excel.Workbook.Names.Add(name, formula);
            InjectedNames.Add(name);
        }
    }

    /// <summary>
    ///     Injects every .lambda file in dependency order. Because Excel validates
    ///     lambda bodies at Names.Add time, a lambda that references another lambda
    ///     will fail to inject if its dependency isn't present yet. We retry until
    ///     the pending set stops shrinking.
    /// </summary>
    private void InjectAllLambdas()
    {
        var lambdasDir = FindLambdasDirectory();
        var files = Directory.GetFiles(lambdasDir, "*.lambda", SearchOption.AllDirectories);

        var pending = new List<(string name, string formula)>();
        foreach (var file in files)
        {
            var (n, f) = LambdaParser.ParseFile(file);
            pending.Add((n, f));
        }

        while (pending.Count > 0)
        {
            var injectedThisPass = new List<(string, string)>();
            foreach (var (n, f) in pending)
            {
                try
                {
                    _excel.Workbook.Names.Add(n, f);
                    InjectedNames.Add(n);
                    injectedThisPass.Add((n, f));
                }
                catch (COMException)
                {
                    // Dependency not yet injected; try again next pass
                }
            }

            if (injectedThisPass.Count == 0)
            {
                var remaining = string.Join(", ", pending.Select(p => p.name));
                throw new InvalidOperationException(
                    $"Could not resolve lambda dependencies. Remaining: {remaining}");
            }

            pending.RemoveAll(p => injectedThisPass.Contains(p));
        }

        _output.WriteLine($"Injected {InjectedNames.Count} lambdas.");
    }

    private void AssertScalar(dynamic cell, object? expected, string testName)
    {
        object? actual = cell.Value;
        _output.WriteLine($"[{testName}] Expected: {expected}, Actual: {actual}");

        if (expected == null)
        {
            Assert.Null(actual);
            return;
        }

        AssertValuesEqual(expected, actual, testName, "");
    }

    private void AssertType(dynamic cell, string expectedType, string testName)
    {
        if (expectedType == "array")
        {
            // A dynamic array formula spills into multiple cells.
            // Check that the spill range has more than one cell.
            try
            {
                var spillRange = cell.SpillingToRange;
                int cellCount = spillRange.Cells.Count;
                _output.WriteLine($"[{testName}] Spill range has {cellCount} cells");
                Assert.True(cellCount > 1,
                    $"[{testName}] Expected array result (spill range > 1 cell), but got {cellCount} cell(s)");
                Marshal.ReleaseComObject(spillRange);
            }
            catch (COMException)
            {
                // SpillingToRange may not be available; fall back to checking the value
                object? val = cell.Value;
                _output.WriteLine($"[{testName}] SpillingToRange not available, value type: {val?.GetType().Name}");
                Assert.NotNull(val);
            }
        }
        else
            throw new NotSupportedException($"Unknown expected_type: {expectedType}");
    }

    private void AssertArray(dynamic cell, List<object> expectedRows, string testName)
    {
        var spillRange = cell.SpillingToRange;
        try
        {
            object[,] values = spillRange.Value;

            var actualRows = values.GetLength(0);
            var actualCols = values.GetLength(1);
            _output.WriteLine($"[{testName}] Array result: {actualRows}x{actualCols}");

            Assert.Equal(expectedRows.Count, actualRows);

            for (var r = 0; r < expectedRows.Count; r++)
                if (expectedRows[r] is List<object> cols)
                {
                    Assert.Equal(cols.Count, actualCols);
                    for (var c = 0; c < cols.Count; c++)
                        AssertValuesEqual(cols[c], values[r + 1, c + 1], testName, $"[{r},{c}]");
                }
                else
                {
                    AssertValuesEqual(expectedRows[r], values[r + 1, 1], testName, $"[{r}]");
                }
        }
        finally
        {
            Marshal.ReleaseComObject(spillRange);
        }
    }

    private void AssertValuesEqual(object expected, object actual, string testName, string position)
    {
        var label = string.IsNullOrEmpty(position) ? $"[{testName}]" : $"[{testName}] {position}";

        if (double.TryParse(Convert.ToString(expected, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var expectedDouble)
            && double.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var actualDouble))
        {
            Assert.True(Math.Abs(expectedDouble - actualDouble) < 1e-10,
                $"{label} Expected {expectedDouble} but got {actualDouble}");
        }
        else
        {
            var expectedStr = Convert.ToString(expected, CultureInfo.InvariantCulture) ?? "";
            var actualStr = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "";
            Assert.Equal(expectedStr, actualStr);
        }
    }

    private static string FormatArg(object arg)
    {
        return arg switch
        {
            bool b => b ? "TRUE" : "FALSE",
            string s when s.StartsWith("=") => s[1..],
            string s => $"\"{s}\"",
            _ => Convert.ToString(arg, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static string FindLambdasDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var lambdasDir = Path.Combine(repoRoot, "lambdas");

        if (!Directory.Exists(lambdasDir))
        {
            throw new DirectoryNotFoundException(
                $"Could not find lambdas directory. Searched: {lambdasDir}");
        }

        return lambdasDir;
    }
}

public class TestSuite
{
    public List<TestCase> Tests { get; set; } = [];
}

public class TestCase
{
    public string Name { get; set; } = "";
    public List<object>? Args { get; set; }
    public object? Expected { get; set; }
    public string? ExpectedType { get; set; }
}