using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AmazonProductExplorer;

/// <summary>Maps documented Oxylabs parsed JSON. Never infers stock from a price or shipping message.</summary>
public static partial class ProductParser
{
    public static List<Product> ParseSearch(JsonElement root)
    {
        var products = new List<Product>();
        foreach (var content in Contents(root))
        {
            if (!content.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object ||
                !results.TryGetProperty("organic", out var organic) || organic.ValueKind != JsonValueKind.Array)
                throw new ScraperException("Oxylabs did not return a valid organic search-results array.");

            foreach (var item in organic.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || IsTrue(item, "is_sponsored")) continue;
                var asin = Text(item, "asin").ToUpperInvariant();
                var title = Text(item, "title");
                if (!AsinPattern().IsMatch(asin) || string.IsNullOrWhiteSpace(title)) continue;
                products.Add(new Product
                {
                    Asin = asin, Title = title, Price = PositiveNumber(item, "price"),
                    Currency = Currency(item), Rating = Rating(item), ReviewCount = Count(item, "reviews_count"),
                    Url = CanonicalUrl(asin), Rank = products.Count + 1
                });
            }
            if (organic.GetArrayLength() > 0 && products.Count == 0)
                throw new ScraperException("Search results were present, but no valid product identifiers could be mapped.");
        }
        return products;
    }

    public static Product ParseProduct(JsonElement root, Product searchProduct)
    {
        var contents = Contents(root).ToList();
        if (contents.Count != 1)
            throw new ScraperException("Oxylabs returned an unexpected number of product-detail records.");
        var content = contents[0];
        var asin = Text(content, "asin").ToUpperInvariant();
        if (asin != searchProduct.Asin)
            throw new ScraperException("Amazon returned a different or missing ASIN; details were not merged into this product.");
        var title = Text(content, "title");
        if (string.IsNullOrWhiteSpace(title))
            throw new ScraperException("Product details did not include a usable title.");

        // Prefer a new, one-time offer. Never combine one offer's stock with another's price.
        JsonElement? selectedOffer = null;
        var hasOffers = content.TryGetProperty("buybox", out var buybox) && buybox.ValueKind == JsonValueKind.Array && buybox.GetArrayLength() > 0;
        if (hasOffers)
        {
            selectedOffer = buybox.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && IsEligibleOffer(x))
                .OrderByDescending(x => IsOneTimeOffer(x))
                .Select(x => (JsonElement?)x).FirstOrDefault();
        }

        var stock = selectedOffer.HasValue ? Text(selectedOffer.Value, "stock")
            : hasOffers ? "" : Text(content, "stock");
        var price = selectedOffer.HasValue ? PositiveNumber(selectedOffer.Value, "price")
            : hasOffers ? null : PositiveNumber(content, "price");
        var (availability, limited) = NormalizeAvailability(stock);
        return new Product
        {
            Asin = asin, Title = title, Price = price, Currency = Currency(content),
            Rating = Rating(content), ReviewCount = Count(content, "reviews_count"),
            Url = CanonicalUrl(asin), Rank = searchProduct.Rank,
            Availability = availability, AvailabilityText = stock, LimitedStock = limited,
            CollectedAt = DateTimeOffset.UtcNow, DetailStatus = "complete",
            DetailNote = hasOffers && !selectedOffer.HasValue ? "No eligible new, one-time buy-box offer was available." : null
        };
    }

    public static (string Status, int? LimitedStock) NormalizeAvailability(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("Unknown", null);
        var normalized = text.ToLowerInvariant();
        if (normalized.Contains("out of stock") || normalized.Contains("currently unavailable") ||
            normalized.Contains("temporarily unavailable") || normalized.Contains("not in stock"))
            return ("Out of stock", null);
        var quantity = StockPattern().Match(normalized);
        if (quantity.Success && int.TryParse(quantity.Groups[1].Value, out var remaining) && remaining > 0)
            return ("Limited stock", remaining);
        if (normalized.Contains("limited stock") || normalized.Contains("only a few left")) return ("Limited stock", null);
        if (normalized.Contains("in stock")) return ("In stock", null);
        return ("Unknown", null);
    }

    private static IEnumerable<JsonElement> Contents(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new ScraperException("Oxylabs returned no parsed result records.");
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object || Count(result, "status_code") is not (>= 200 and < 300))
                throw new ScraperException("Amazon did not return a successful page to Oxylabs.");
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
                throw new ScraperException("Oxylabs returned unparsed or malformed content. Confirm parsing is enabled.");
            if (content.TryGetProperty("parse_status_code", out _) && Count(content, "parse_status_code") != 12000)
                throw new ScraperException("Oxylabs reported a parser warning or failure. This record needs review.");
            yield return content;
        }
    }

    private static bool IsEligibleOffer(JsonElement offer)
    {
        var label = (Text(offer, "condition") + " " + Text(offer, "name")).ToLowerInvariant();
        return !label.Contains("used") && !label.Contains("renewed") && !label.Contains("refurbished") &&
            !label.Contains("subscribe") && !label.Contains("subscription") && !label.Contains("rental");
    }

    private static bool IsOneTimeOffer(JsonElement offer)
    {
        var label = (Text(offer, "condition") + " " + Text(offer, "name")).ToLowerInvariant();
        return label.Contains("one-time") || label.Contains("one time") || label.Trim() == "new";
    }

    public static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    private static decimal? Number(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static decimal? PositiveNumber(JsonElement element, string key) => Number(element, key) is > 0 and var number ? number : null;
    private static decimal? Rating(JsonElement element) => Number(element, "rating") is > 0 and <= 5 and var rating ? rating : null;
    private static int? Count(JsonElement element, string key) => Number(element, key) is >= 0 and <= int.MaxValue and var number && decimal.Truncate(number) == number ? (int)number : null;
    private static bool IsTrue(JsonElement element, string key) => element.TryGetProperty(key, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
    private static string Currency(JsonElement element) => Text(element, "currency") is { Length: 3 } currency ? currency.ToUpperInvariant() : "USD";
    private static string CanonicalUrl(string asin) => $"https://www.amazon.com/dp/{asin}";

    [GeneratedRegex("^[A-Z0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex AsinPattern();
    [GeneratedRegex(@"\bonly\s+(\d+)\s+(?:left|remaining)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StockPattern();
}
