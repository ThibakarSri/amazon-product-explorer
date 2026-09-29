using System.Globalization;
using System.Text;

namespace AmazonProductExplorer;

public static class CsvExporter
{
    public static string Export(ScrapeJob job)
    {
        var output = new StringBuilder();
        output.AppendLine("Mode,Query,Delivery ZIP,Rank,ASIN,Product Title,Product Price,Currency,Rating,Review Count,Availability,Availability Text,Reported Remaining Quantity,Product Link,Collected At,Detail Status,Detail Note");
        foreach (var p in job.Products)
        {
            string[] cells = [job.Mode, job.Query, job.ZipCode, p.Rank.ToString(CultureInfo.InvariantCulture), p.Asin, p.Title,
                p.Price?.ToString(CultureInfo.InvariantCulture) ?? "", p.Currency, p.Rating?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.ReviewCount?.ToString(CultureInfo.InvariantCulture) ?? "", p.Availability, p.AvailabilityText,
                p.LimitedStock?.ToString(CultureInfo.InvariantCulture) ?? "", p.Url, p.CollectedAt.ToString("O"), p.DetailStatus, p.DetailNote ?? ""];
            output.AppendLine(string.Join(",", cells.Select(EscapeCell)));
        }
        return output.ToString();
    }

    // Treat untrusted titles/queries as text when opened by spreadsheet software.
    public static string EscapeCell(string value)
    {
        var first = value.AsSpan().TrimStart();
        if (value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n') ||
            first.Length > 0 && first[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
