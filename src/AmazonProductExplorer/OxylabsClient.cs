using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AmazonProductExplorer;

/// <summary>Push-Pull API: submit once, poll status, then retrieve parsed results over HTTPS.</summary>
public sealed class OxylabsClient(HttpClient http, OxylabsOptions options)
{
    /// <summary>Uses the free usage endpoint; does not submit a scraping job or expose account statistics.</summary>
    public async Task CheckAccessAsync(CancellationToken token)
    {
        if (!options.IsConfigured)
            throw new ScraperException("Oxylabs credentials are missing. Save the Web Scraper API username and password using scripts/setup-oxylabs.sh.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var result = await SendAsync(HttpMethod.Get, "v2/stats", null, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ScraperException("The Oxylabs connection check timed out. No scraping job was submitted.");
        }
        catch (HttpRequestException)
        {
            throw new ScraperException("Could not reach Oxylabs. Check your network connection. No scraping job was submitted.");
        }
        catch (JsonException)
        {
            throw new ScraperException("Oxylabs returned an unexpected response to the connection check. No scraping job was submitted.");
        }
    }

    public Task<JsonDocument> SearchAsync(string query, string zipCode, int page, CancellationToken token) =>
        ExecuteAsync(new
        {
            source = "amazon_search", domain = "com", query, parse = true, geo_location = zipCode,
            locale = "en-US", pages = 1, start_page = page,
            context = new[] { new { key = "currency", value = "USD" }, new { key = "sort_by", value = "featured" } }
        }, token);

    public Task<JsonDocument> ProductAsync(string asin, string zipCode, CancellationToken token) =>
        ExecuteAsync(new
        {
            source = "amazon_product", domain = "com", query = asin, parse = true, geo_location = zipCode,
            locale = "en-US",
            // Do not automatically replace a search ASIN with another variation.
            context = new[] { new { key = "currency", value = (object)"USD" }, new { key = "autoselect_variant", value = (object)false } }
        }, token);

    private async Task<JsonDocument> ExecuteAsync(object payload, CancellationToken token)
    {
        if (!options.IsConfigured) throw new ScraperException("Oxylabs credentials are not configured on the server.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.JobTimeoutSeconds));
        try
        {
            // Never automatically retry POST: a lost response could still have created a billable job.
            using var submitted = await SendAsync(HttpMethod.Post, "v1/queries", payload, timeout.Token);
            var id = ProductParser.Text(submitted.RootElement, "id");
            if (string.IsNullOrEmpty(id) || id.Length > 100 || !id.All(char.IsAsciiLetterOrDigit))
                throw new ScraperException("Oxylabs did not return a valid job ID. Check your Oxylabs dashboard before trying again.");
            var statusPath = $"v1/queries/{id}";
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), timeout.Token);
                using var status = await SendAsync(HttpMethod.Get, statusPath, null, timeout.Token);
                switch (ProductParser.Text(status.RootElement, "status"))
                {
                    case "done": return await SendAsync(HttpMethod.Get, $"{statusPath}/results", null, timeout.Token);
                    case "faulted": throw new ScraperException("Oxylabs could not complete this scrape. Check its dashboard for details.");
                    case "pending": break;
                    default: throw new ScraperException("Oxylabs returned an unexpected job status.");
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ScraperException("This Oxylabs job exceeded the configured timeout. A submitted provider job may still finish and incur a charge.");
        }
        catch (HttpRequestException)
        {
            throw new ScraperException("Could not reach Oxylabs. A submitted provider job may still exist; check its dashboard before retrying.");
        }
        catch (JsonException)
        {
            throw new ScraperException("Oxylabs returned a response that was not valid JSON.");
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));
            if (payload is not null) request.Content = JsonContent.Create(payload);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (method == HttpMethod.Get && attempt < 2 &&
                (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500))
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter ?? Math.Pow(2, attempt + 1), 1, 30)), token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Oxylabs HTTP 401: authentication was rejected. Check or reset your Web Scraper API username and password in the Oxylabs dashboard, save both again, then restart this app. Use API user credentials, not your dashboard email/password, proxy credentials or an API token.",
                    HttpStatusCode.Forbidden => "Oxylabs HTTP 403: this account is not allowed to access the requested resource. Check Web Scraper API access and account status with Oxylabs. This response does not establish that the password is wrong.",
                    HttpStatusCode.TooManyRequests => "Oxylabs rate-limited the request. Lower concurrency or wait before starting another job.",
                    HttpStatusCode.BadRequest => "Oxylabs rejected the request parameters. Check the current API documentation and your account capabilities.",
                    _ => $"Oxylabs returned HTTP {(int)response.StatusCode}. Check its dashboard for details."
                };
                throw new ScraperException(message);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token);
        }
    }
}
