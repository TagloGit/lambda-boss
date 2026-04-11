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
            var workbook = _excel.Workbook;
            workbook.Names.Add("TEST_DOUBLE", "=LAMBDA(x, x*2)");

            // Verify the name exists
            var name = workbook.Names.Item("TEST_DOUBLE");
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
        var workbook = _excel.Workbook;

        // Add initial
        workbook.Names.Add("TEST_UPDATABLE", "=LAMBDA(x, x*2)");

        // Update it
        var name = workbook.Names.Item("TEST_UPDATABLE");
        name.RefersTo = "=LAMBDA(x, x*3)";

        string refersTo = name.RefersTo;
        _output.WriteLine($"Updated TEST_UPDATABLE RefersTo: {refersTo}");

        Assert.Contains("x*3", refersTo);

        // Cleanup
        name.Delete();
        Marshal.ReleaseComObject(name);
    }

    [Fact]
    public void UpdateFlow_OverwriteExistingLambda_ReflectsNewFormula()
    {
        var ws = _excel.AddWorksheet();
        try
        {
            var workbook = _excel.Workbook;

            // Simulate initial load: inject a LAMBDA with comment
            workbook.Names.Add("tst.Double", "=LAMBDA(x, x*2)");
            var name = workbook.Names.Item("tst.Double");
            name.Comment = "[LambdaBoss] https://github.com/TestOwner/repo|test|tst";

            // Verify initial value
            var cell = ws.Range["A1"];
            try
            {
                cell.Formula2 = "=tst.Double(5)";
                Thread.Sleep(500);
                Assert.Equal(10.0, Convert.ToDouble(cell.Value));
            }
            finally
            {
                Marshal.ReleaseComObject(cell);
            }

            // Verify comment was stamped
            string comment = name.Comment;
            _output.WriteLine($"Comment: {comment}");
            Assert.StartsWith("[LambdaBoss]", comment);

            // Simulate "update": overwrite with new formula (x*3 instead of x*2)
            name.RefersTo = "=LAMBDA(x, x*3)";

            // Verify updated value propagates
            var cell2 = ws.Range["A2"];
            try
            {
                cell2.Formula2 = "=tst.Double(5)";
                Thread.Sleep(500);
                object? value = cell2.Value;
                _output.WriteLine($"After update: =tst.Double(5) = {value}");
                Assert.Equal(15.0, Convert.ToDouble(value));
            }
            finally
            {
                Marshal.ReleaseComObject(cell2);
            }

            // Also verify the existing cell A1 recalculated
            var cell1Again = ws.Range["A1"];
            try
            {
                Thread.Sleep(500);
                object? updatedValue = cell1Again.Value;
                _output.WriteLine($"Cell A1 after update: {updatedValue}");
                Assert.Equal(15.0, Convert.ToDouble(updatedValue));
            }
            finally
            {
                Marshal.ReleaseComObject(cell1Again);
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
                // ignored
            }
        }
    }
}