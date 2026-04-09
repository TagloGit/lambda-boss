using System.Runtime.InteropServices;

using Xunit;
using Xunit.Abstractions;

namespace LambdaBoss.AddinTests;

[Collection("Excel Addin")]
public class SmokeTests
{
    private readonly ExcelAddinFixture _excel;
    private readonly ITestOutputHelper _output;

    public SmokeTests(ExcelAddinFixture excel, ITestOutputHelper output)
    {
        _excel = excel;
        _output = output;
    }

    [Fact]
    public void AddinLoadsSuccessfully()
    {
        Assert.NotNull(_excel.Application);
        _output.WriteLine("Excel launched and add-in registered successfully.");
    }

    [Fact]
    public void InjectLambda_CreatesNameInWorkbook()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            // Inject a test LAMBDA via Name Manager
            dynamic workbook = _excel.Workbook;
            workbook.Names.Add("TEST_DOUBLE", "=LAMBDA(x, x*2)");

            // Verify the name exists
            dynamic name = workbook.Names.Item("TEST_DOUBLE");
            string refersTo = name.RefersTo;
            _output.WriteLine($"TEST_DOUBLE RefersTo: {refersTo}");

            Assert.Contains("LAMBDA", refersTo);

            // Verify it works as a formula
            var cell = ws.Range["A1"];
            try
            {
                cell.Formula2 = "=TEST_DOUBLE(5)";
                // Allow calc
                Thread.Sleep(500);
                object? value = cell.Value;
                _output.WriteLine($"=TEST_DOUBLE(5) result: {value}");
                Assert.Equal(10.0, Convert.ToDouble(value));
            }
            finally
            {
                Marshal.ReleaseComObject(cell);
            }

            // Cleanup
            name.Delete();
            Marshal.ReleaseComObject(name);
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

    [Fact]
    public void InjectLambda_UpdatesExistingName()
    {
        dynamic workbook = _excel.Workbook;

        // Add initial
        workbook.Names.Add("TEST_UPDATABLE", "=LAMBDA(x, x*2)");

        // Update it
        dynamic name = workbook.Names.Item("TEST_UPDATABLE");
        name.RefersTo = "=LAMBDA(x, x*3)";

        string refersTo = name.RefersTo;
        _output.WriteLine($"Updated TEST_UPDATABLE RefersTo: {refersTo}");

        Assert.Contains("x*3", refersTo);

        // Cleanup
        name.Delete();
        Marshal.ReleaseComObject(name);
    }
}
