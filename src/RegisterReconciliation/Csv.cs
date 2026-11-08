using System.Globalization;
using System.Text.RegularExpressions;

namespace RegisterReconciliation;

internal sealed record Operation(long Id, string? ExternalId, decimal Amount, string Currency,
    DateTime OccurredAt, string OrderReference);

internal static partial class Csv
{
    internal const string Header = "external_id,amount,currency,occurred_at,order_reference";

    [GeneratedRegex("""\G(?:"((?:[^"]|"")*)"|([^",]*))(,|$)""", RegexOptions.CultureInvariant)]
    private static partial Regex Field();

    [GeneratedRegex(@"\A[+-]?[0-9]{1,16}(?:\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Money();

    [GeneratedRegex(@"\A[A-Z]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Currency();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    internal static (Operation? Row, string? Error) Parse(string line)
    {
        if (line.Length > 4096 || line.Any(char.IsControl))
            return (null, "Запись длиннее 4096 символов или содержит управляющие символы.");

        var fields = new string[5];
        var offset = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var match = Field().Match(line, offset);
            if (!match.Success || (match.Groups[3].Length == 0) != (i == fields.Length - 1))
                return (null, "Ожидаются пять CSV-полей.");

            fields[i] = match.Groups[1].Success
                ? match.Groups[1].Value.Replace("\"\"", "\"")
                : match.Groups[2].Value;
            offset += match.Length;
        }

        var errors = new List<string>();
        if (fields[0].Length != 0 && !Identifier().IsMatch(fields[0]))
            errors.Add("external_id");
        if (!Money().IsMatch(fields[1]))
            errors.Add("amount");
        if (!Currency().IsMatch(fields[2]))
            errors.Add("currency");

        string[] timestampFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (!DateTimeOffset.TryParseExact(fields[3], timestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time) || time.UtcDateTime.Year is < 2 or > 9998 ||
            time.UtcTicks % 10 != 0)
            errors.Add("occurred_at");

        if (fields[4].Length is < 1 or > 128 || fields[4] != fields[4].Trim())
            errors.Add("order_reference");

        if (errors.Count > 0)
            return (null, "Некорректные поля: " + string.Join(", ", errors));

        return (new Operation(0, fields[0].Length == 0 ? null : fields[0],
            decimal.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], time.UtcDateTime, fields[4]), null);
    }

    internal static string Cell(string? value)
    {
        if (value is null)
            return "";

        var trimmed = value.TrimStart();
        if ((value.Length > 0 && value[0] is '\t' or '\r' or '\n') ||
            (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠'))
            value = "'" + value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
