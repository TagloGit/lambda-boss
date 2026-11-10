namespace LambdaBoss;

/// <summary>
///     How many arguments native Excel functions take (issue #424), for Check Formula. Only
///     functions whose counts are certain are listed; any other function's count isn't checked,
///     because a wrong entry would be a false alarm. 255 means "any number". An empty argument
///     (<c>XLOOKUP(a,b,c,,2)</c>) counts.
/// </summary>
internal static class ExcelFunctionArity
{
    private const string Table = @"
ABS 1 1|ADDRESS 2 5|AGGREGATE 3 255|AND 1 255|ARRAYTOTEXT 1 2|AVERAGE 1 255|AVERAGEA 1 255|AVERAGEIF 2 3|
AVERAGEIFS 3 255|BASE 2 3|BIN2DEC 1 1|BITAND 2 2|BITOR 2 2|BITXOR 2 2|BITLSHIFT 2 2|BITRSHIFT 2 2|
BYCOL 2 2|BYROW 2 2|CEILING.MATH 1 3|CHAR 1 1|CHOOSE 2 255|CHOOSECOLS 2 255|CHOOSEROWS 2 255|CLEAN 1 1|
CODE 1 1|COLUMN 0 1|COLUMNS 1 1|COMBIN 2 2|CONCAT 1 255|CONCATENATE 1 255|COUNT 1 255|COUNTA 1 255|
COUNTBLANK 1 1|COUNTIF 2 2|COUNTIFS 2 255|DATE 3 3|DATEDIF 3 3|DATEVALUE 1 1|DAY 1 1|DAYS 2 2|
DEC2BIN 1 2|DEC2HEX 1 2|DROP 2 3|EDATE 2 2|EOMONTH 2 2|EVEN 1 1|EXACT 2 2|EXP 1 1|EXPAND 2 4|FACT 1 1|
FALSE 0 0|FILTER 2 3|FIND 2 3|FIXED 1 3|FLOOR.MATH 1 3|FREQUENCY 2 2|GCD 1 255|GROUPBY 3 8|HEX2DEC 1 1|
HLOOKUP 3 4|HOUR 1 1|HSTACK 1 255|HYPERLINK 1 2|IF 2 3|IFERROR 2 2|IFNA 2 2|IFS 2 254|INDEX 2 4|
INDIRECT 1 2|INT 1 1|ISBLANK 1 1|ISERR 1 1|ISERROR 1 1|ISEVEN 1 1|ISFORMULA 1 1|ISLOGICAL 1 1|ISNA 1 1|
ISNONTEXT 1 1|ISNUMBER 1 1|ISODD 1 1|ISOMITTED 1 1|ISREF 1 1|ISTEXT 1 1|LARGE 2 2|LCM 1 255|LEFT 1 2|
LEN 1 1|LN 1 1|LOG 1 2|LOG10 1 1|LOOKUP 2 3|LOWER 1 1|MAKEARRAY 3 3|MAP 2 255|MATCH 2 3|MAX 1 255|
MAXIFS 3 255|MDETERM 1 1|MEDIAN 1 255|MID 3 3|MIN 1 255|MINIFS 3 255|MINUTE 1 1|MINVERSE 1 1|MMULT 2 2|
MOD 2 2|MONTH 1 1|MROUND 2 2|MUNIT 1 1|N 1 1|NA 0 0|NETWORKDAYS 2 3|NOT 1 1|NOW 0 0|NUMBERVALUE 1 3|
ODD 1 1|OFFSET 3 5|OR 1 255|PERMUT 2 2|PI 0 0|PIVOTBY 4 11|POWER 2 2|PRODUCT 1 255|PROPER 1 1|
QUOTIENT 2 2|RAND 0 0|RANDARRAY 0 5|RANDBETWEEN 2 2|RANK.EQ 2 3|REDUCE 3 3|REGEXEXTRACT 2 4|
REGEXREPLACE 3 5|REGEXTEST 2 3|REPLACE 4 4|REPT 2 2|RIGHT 1 2|ROUND 2 2|ROUNDDOWN 2 2|ROUNDUP 2 2|
ROW 0 1|ROWS 1 1|SCAN 3 3|SEARCH 2 3|SECOND 1 1|SEQUENCE 1 4|SIGN 1 1|SMALL 2 2|SORT 1 4|SORTBY 2 255|
SQRT 1 1|SUBSTITUTE 3 4|SUBTOTAL 2 255|SUM 1 255|SUMIF 2 3|SUMIFS 3 255|SUMPRODUCT 1 255|SWITCH 3 254|
T 1 1|TAKE 2 3|TEXT 2 2|TEXTAFTER 2 6|TEXTBEFORE 2 6|TEXTJOIN 3 255|TEXTSPLIT 2 6|TIME 3 3|
TIMEVALUE 1 1|TOCOL 1 3|TODAY 0 0|TOROW 1 3|TRANSPOSE 1 1|TRIM 1 1|TRIMRANGE 1 3|TRUE 0 0|TRUNC 1 2|
TYPE 1 1|UNICHAR 1 1|UNICODE 1 1|UNIQUE 1 3|UPPER 1 1|VALUE 1 1|VALUETOTEXT 1 2|VLOOKUP 3 4|
VSTACK 1 255|WEEKDAY 1 2|WORKDAY 2 3|WRAPCOLS 2 3|WRAPROWS 2 3|XLOOKUP 3 6|XMATCH 2 4|XOR 1 255|YEAR 1 1";

    private static readonly Dictionary<string, (int Min, int Max)> Counts = Parse();

    private static Dictionary<string, (int, int)> Parse()
    {
        var d = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Table.Split('|'))
        {
            var parts = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
                d[parts[0]] = (int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }

        return d;
    }

    /// <summary>The argument range of a native function, unprefixed, if it is known.</summary>
    public static bool TryGet(string name, out (int Min, int Max) range) => Counts.TryGetValue(name, out range);

    internal static int Count => Counts.Count;
}
