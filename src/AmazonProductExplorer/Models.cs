namespace AmazonProductExplorer;

public sealed record CreateJobRequest(string? Query, int Limit = 10, string? ZipCode = "10001", string? Mode = "demo");

public sealed class Product
{
    public string Asin { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal? Price { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal? Rating { get; set; }
    public int? ReviewCount { get; set; }
    public string Url { get; set; } = "";
    public string Availability { get; set; } = "Unknown";
    public string AvailabilityText { get; set; } = "";
    public int? LimitedStock { get; set; }
    public int Rank { get; set; }
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
    public string DetailStatus { get; set; } = "pending";
    public string? DetailNote { get; set; }
}

public sealed class ScrapeJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Query { get; set; } = "";
    public int Limit { get; set; }
    public string ZipCode { get; set; } = "10001";
    public string Mode { get; set; } = "demo";
    public string Status { get; set; } = "queued";
    public string Message { get; set; } = "Waiting for the local background worker.";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int CompletedDetails { get; set; }
    public int FailedDetails { get; set; }
    public int SearchPages { get; set; }
    public bool CancellationRequested { get; set; }
    public List<Product> Products { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public static bool IsTerminal(string status) => status is "completed" or "partial" or "failed" or "cancelled" or "interrupted";
}

public sealed class OxylabsOptions
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int MaxSearchPages { get; set; } = 6;
    public int MaxConcurrentDetails { get; set; } = 3;
    public int PollIntervalSeconds { get; set; } = 3;
    public int JobTimeoutSeconds { get; set; } = 180;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public void Validate()
    {
        if (MaxSearchPages is < 1 or > 20 || MaxConcurrentDetails is < 1 or > 10 ||
            PollIntervalSeconds is < 1 or > 30 || JobTimeoutSeconds is < 15 or > 900)
            throw new InvalidOperationException("Invalid Oxylabs limits. Check appsettings.json and the setup guide.");
    }
}

// Only this exception's deliberately sanitized message may be shown to users.
public sealed class ScraperException(string message) : Exception(message);
