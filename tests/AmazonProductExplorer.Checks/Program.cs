using System.Text.Json;
using AmazonProductExplorer;

var checks = new List<(string Name, Action Run)>
{
    ("organic only; sponsored and malformed listings excluded", () =>
    {
        using var data = Search("""
            {"asin":"B000000001","title":"Headphones","price":49.95,"rating":4.5,"reviews_count":32},
            {"asin":"B000000002","title":"Ad","is_sponsored":true},
            {"asin":"invalid","title":"Invalid"}
            """);
        var products = ProductParser.ParseSearch(data.RootElement);
        Equal(1, products.Count);
        Equal(49.95m, products[0].Price);
        Equal("https://www.amazon.com/dp/B000000001", products[0].Url);
    }),
    ("missing values are null, not free or zero-rated", () =>
    {
        using var data = Search("""{"asin":"B000000001","title":"Unknown","price":0,"rating":0}""");
        var product = ProductParser.ParseSearch(data.RootElement).Single();
        Equal<decimal?>(null, product.Price);
        Equal<decimal?>(null, product.Rating);
        Equal<int?>(null, product.ReviewCount);
        Equal("Unknown", product.Availability);
    }),
    ("empty organic results are valid", () =>
    {
        using var data = Search("");
        Equal(0, ProductParser.ParseSearch(data.RootElement).Count);
    }),
    ("unparsed HTML and HTTP failures are rejected", () =>
    {
        foreach (var json in new[] {
            """{"results":[{"status_code":200,"content":"<html>Blocked</html>"}]}""",
            """{"results":[{"status_code":503,"content":{"results":{"organic":[]}}}]}""",
            """{"results":[{"status_code":200,"content":{"parse_status_code":12001,"results":{"organic":[]}}}]}""",
            """{"results":[{"status_code":200,"content":{"unexpected":[]}}]}""" })
        {
            using var data = JsonDocument.Parse(json);
            Throws(() => ProductParser.ParseSearch(data.RootElement));
        }
    }),
    ("limited-stock quantity requires explicit listing text", () =>
    {
        Equal(("Limited stock", (int?)3), ProductParser.NormalizeAvailability("Only 3 left in stock - order soon."));
        Equal(("Limited stock", (int?)null), ProductParser.NormalizeAvailability("Limited stock"));
        Equal(("Unknown", (int?)null), ProductParser.NormalizeAvailability("Usually ships in 3 days"));
        Equal(("Unknown", (int?)null), ProductParser.NormalizeAvailability(null));
        Equal(("Out of stock", (int?)null), ProductParser.NormalizeAvailability("Currently unavailable."));
        Equal(("Out of stock", (int?)null), ProductParser.NormalizeAvailability("Not in stock"));
    }),
    ("buy-box price and stock are taken from the same eligible offer", () =>
    {
        using var data = Detail("""
            "asin":"B000000001","title":"Headphones","price":5,"stock":"In stock", "currency":"USD",
            "buybox":[{"condition":"Used","price":10,"stock":"In stock"},
            {"name":"Subscribe & Save","price":20,"stock":"In stock"},
            {"name":"One-time purchase","condition":"New","price":30,"stock":"Only 2 left in stock"}]
            """);
        var product = ProductParser.ParseProduct(data.RootElement, Original());
        Equal(30m, product.Price);
        Equal("Limited stock", product.Availability);
        Equal<int?>(2, product.LimitedStock);
        Equal(7, product.Rank);
    }),
    ("different variants are not silently merged", () =>
    {
        using var data = Detail("\"asin\":\"B000000002\",\"title\":\"Different variant\"");
        Throws(() => ProductParser.ParseProduct(data.RootElement, Original()));
    }),
    ("used-only offers are not presented as new-product prices", () =>
    {
        using var data = Detail("""
            "asin":"B000000001","title":"Headphones","price":10,"stock":"In stock",
            "buybox":[{"condition":"Used","price":10,"stock":"In stock"}]
            """);
        var product = ProductParser.ParseProduct(data.RootElement, Original());
        Equal<decimal?>(null, product.Price);
        Equal("Unknown", product.Availability);
    }),
    ("CSV cells cannot activate spreadsheet formulas", () =>
    {
        Equal("\"'=1+1\"", CsvExporter.EscapeCell("=1+1"));
        Equal("\"'  @SUM(A1)\"", CsvExporter.EscapeCell("  @SUM(A1)"));
        Equal("\"a,\"\"quoted\"\" title\"", CsvExporter.EscapeCell("a,\"quoted\" title"));
    }),
    ("out-of-range ratings are discarded", () =>
    {
        using var data = Search("""{"asin":"B000000001","title":"Invalid rating","rating":6,"price":-1}""");
        var product = ProductParser.ParseSearch(data.RootElement).Single();
        Equal<decimal?>(null, product.Rating);
        Equal<decimal?>(null, product.Price);
    })
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} parser checks passed.");
return failed == 0 ? 0 : 1;

static JsonDocument Search(string rows) => JsonDocument.Parse(
    "{\"results\":[{\"status_code\":200,\"content\":{\"parse_status_code\":12000,\"results\":{\"organic\":[" + rows + "]}}}]}");
static JsonDocument Detail(string fields) => JsonDocument.Parse(
    "{\"results\":[{\"status_code\":200,\"content\":{\"parse_status_code\":12000," + fields + "}}]}");
static Product Original() => new() { Asin = "B000000001", Title = "Original", Rank = 7 };
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; received {actual}.");
}
static void Throws(Action action)
{
    try { action(); } catch (ScraperException) { return; }
    throw new Exception("Expected a safe parser error.");
}
