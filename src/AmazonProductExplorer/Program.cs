using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AmazonProductExplorer;

var checkOxylabs = args.Contains("--check-oxylabs", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--check-oxylabs").ToArray());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
var oxylabs = builder.Configuration.GetSection("Oxylabs").Get<OxylabsOptions>() ?? new OxylabsOptions();
oxylabs.Validate();
builder.Services.AddSingleton(oxylabs);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddHttpClient<OxylabsClient>(http =>
{
    http.BaseAddress = new Uri("https://data.oxylabs.io/");
    http.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<ScrapeWorker>();
var app = builder.Build();

if (checkOxylabs)
{
    try
    {
        await app.Services.GetRequiredService<OxylabsClient>().CheckAccessAsync(CancellationToken.None);
        Console.WriteLine("Oxylabs account check succeeded (HTTP 200). No scraping job was submitted. This confirms usage-endpoint access, not Amazon scraping access or remaining credits.");
    }
    catch (ScraperException exception)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }
    finally { await app.DisposeAsync(); }
    return;
}

app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
    var host = context.Request.Host.Host;
    if (remote is null || !IPAddress.IsLoopback(remote) ||
        !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1"))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "This learning project accepts local connections only." });
        return;
    }
    if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var sameOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            !string.IsNullOrEmpty(origin) && !origin.Equals(sameOrigin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Cross-site requests are not allowed." });
            return;
        }
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
    {
        app.Logger.LogError("Request failed with error type {ErrorType}.", ex.GetType().Name);
        context.Response.StatusCode = ex is BadHttpRequestException bad ? bad.StatusCode : 500;
        await context.Response.WriteAsJsonAsync(new { error = ex is BadHttpRequestException ? "The request body is invalid." : "The local server could not complete this request. Check storage permissions and server logs." });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", () => Results.Ok(new { liveConfigured = oxylabs.IsConfigured, maxProducts = 60 }));
app.MapPost("/api/jobs", (CreateJobRequest request, JobStore store) =>
{
    var query = request.Query?.Trim() ?? "";
    if (query.Length is < 2 or > 120 || query.Any(char.IsControl))
        return Results.BadRequest(new { error = "Enter a product keyword between 2 and 120 characters." });
    if (request.Limit is < 1 or > 60)
        return Results.BadRequest(new { error = "Choose between 1 and 60 products." });
    if (request.ZipCode is null || !Regex.IsMatch(request.ZipCode, "^[0-9]{5}$", RegexOptions.CultureInvariant))
        return Results.BadRequest(new { error = "Enter a 5-digit US delivery ZIP code." });
    if (request.Mode is not ("demo" or "live"))
        return Results.BadRequest(new { error = "Choose demo or live mode." });
    if (request.Mode == "live" && !oxylabs.IsConfigured)
        return Results.BadRequest(new { error = "Configure Oxylabs credentials on the server before using live mode. Demo mode works without credentials." });
    var job = store.TryCreate(request with { Query = query });
    return job is null ? Results.Json(new { error = "Three jobs are already active or queued. Wait for one to finish." }, statusCode: 429)
        : Results.Accepted($"/api/jobs/{job.Id}", new { id = job.Id });
});
app.MapGet("/api/jobs/{id}", (string id, JobStore store) => store.Get(id) is { } job
    ? Results.Ok(job) : Results.NotFound(new { error = "This job was not found." }));
app.MapPost("/api/jobs/{id}/cancel", (string id, JobStore store) => store.Cancel(id)
    ? Results.Ok(new { message = "Cancellation requested, or the job has already finished." })
    : Results.NotFound(new { error = "This job was not found." }));
app.MapGet("/api/jobs/{id}/export", (string id, JobStore store) => store.Get(id) is { } job
    ? Results.File(Encoding.UTF8.GetBytes("\uFEFF" + CsvExporter.Export(job)), "text/csv; charset=utf-8", $"amazon-{job.Mode}-{job.Id}.csv")
    : Results.NotFound(new { error = "This job was not found." }));
app.Run();
