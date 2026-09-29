namespace AmazonProductExplorer;

public sealed class ScrapeWorker(JobStore store, OxylabsClient client, OxylabsOptions options, ILogger<ScrapeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in store.ReadAllAsync(stoppingToken))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, store.Token(id));
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var job = store.Get(id)!;
                if (job.Mode == "demo") await RunDemoAsync(job, linked.Token);
                else await RunLiveAsync(job, linked.Token);
            }
            catch (OperationCanceledException)
            {
                store.Update(id, job =>
                {
                    job.Status = stoppingToken.IsCancellationRequested ? "interrupted" : "cancelled";
                    job.Message = stoppingToken.IsCancellationRequested
                        ? "Server stopped. Saved results remain available; start a new job to retry."
                        : "Cancelled. Saved results remain available. Submitted Oxylabs jobs may still run and incur charges.";
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning("Job {JobId} stopped with error type {ErrorType}.", id, ex.GetType().Name);
                store.Update(id, job =>
                {
                    job.Status = job.Products.Count > 0 ? "partial" : "failed";
                    job.Message = ex is ScraperException ? ex.Message : "The local job could not finish. Check server logs and storage permissions.";
                    job.Warnings.Add(job.Message);
                });
            }
            finally { store.Release(id); }
        }
    }

    private async Task RunLiveAsync(ScrapeJob job, CancellationToken token)
    {
        var found = new List<Product>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var incompleteSearch = false;
        var exhausted = false;
        store.Update(job.Id, x => { x.Status = "searching"; x.Message = "Finding unique organic products in Amazon's featured search order."; });
        for (var page = 1; page <= options.MaxSearchPages && found.Count < job.Limit; page++)
        {
            token.ThrowIfCancellationRequested();
            List<Product> pageProducts;
            try
            {
                using var response = await client.SearchAsync(job.Query, job.ZipCode, page, token);
                pageProducts = ProductParser.ParseSearch(response.RootElement);
            }
            catch (ScraperException ex) when (found.Count > 0)
            {
                incompleteSearch = true;
                store.Update(job.Id, x => x.Warnings.Add($"Search stopped on page {page}: {ex.Message}"));
                break;
            }
            var before = found.Count;
            foreach (var product in pageProducts)
            {
                if (found.Count >= job.Limit) break;
                if (!seen.Add(product.Asin)) continue;
                product.Rank = found.Count + 1;
                found.Add(product);
            }
            store.Update(job.Id, x =>
            {
                x.Products = found.ToList(); x.SearchPages = page;
                x.Message = $"Found {found.Count} of up to {job.Limit} unique products after {page} search page(s).";
            });
            if (pageProducts.Count == 0) { exhausted = true; break; }
            if (found.Count == before)
            {
                incompleteSearch = true;
                store.Update(job.Id, x => x.Warnings.Add("The next search page repeated earlier products. Pagination stopped to limit API usage."));
                break;
            }
        }
        if (found.Count < job.Limit && !exhausted && !incompleteSearch)
        {
            incompleteSearch = true;
            store.Update(job.Id, x => x.Warnings.Add($"Reached the configured limit of {options.MaxSearchPages} search pages before finding {job.Limit} unique products."));
        }
        if (found.Count == 0)
        {
            store.Update(job.Id, x => { x.Status = "completed"; x.Message = "Amazon returned no matching organic products."; });
            return;
        }

        store.Update(job.Id, x => { x.Status = "enriching"; x.Message = $"Checking detail pages for {found.Count} products."; });
        await Parallel.ForEachAsync(found, new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrentDetails, CancellationToken = token }, async (product, detailToken) =>
        {
            try
            {
                using var response = await client.ProductAsync(product.Asin, job.ZipCode, detailToken);
                var detailed = ProductParser.ParseProduct(response.RootElement, product);
                store.Update(job.Id, x =>
                {
                    x.Products[x.Products.FindIndex(p => p.Asin == product.Asin)] = detailed;
                    x.CompletedDetails++;
                    x.Message = $"{x.CompletedDetails} details collected; {x.FailedDetails} failed; {x.Products.Count} products selected.";
                });
            }
            catch (ScraperException ex)
            {
                store.Update(job.Id, x =>
                {
                    var failed = x.Products.First(p => p.Asin == product.Asin);
                    failed.DetailStatus = "failed";
                    failed.DetailNote = ex.Message;
                    x.FailedDetails++;
                    x.Message = $"{x.CompletedDetails} details collected; {x.FailedDetails} failed; {x.Products.Count} products selected.";
                });
            }
        });
        token.ThrowIfCancellationRequested();
        store.Update(job.Id, x =>
        {
            x.Status = incompleteSearch || x.FailedDetails > 0 ? "partial" : "completed";
            x.Message = $"Collected {x.Products.Count} unique products and {x.CompletedDetails} product-detail pages.";
            if (x.FailedDetails > 0) x.Warnings.Add($"{x.FailedDetails} detail pages failed. Their rows retain search-page data and unknown availability; see each row's detail note.");
            x.Warnings.Add("Snapshot only: prices and stock can change. Limited-stock counts reflect the selected offer's message, not total Amazon inventory.");
        });
    }

    private async Task RunDemoAsync(ScrapeJob job, CancellationToken token)
    {
        store.Update(job.Id, x => { x.Status = "searching"; x.Message = "Building illustrative sample records. No Amazon requests are made."; });
        await Task.Delay(350, token);
        string[] styles = ["Everyday", "Compact", "Premium", "Essential", "Travel", "Studio", "Classic", "Performance"];
        var products = Enumerable.Range(1, job.Limit).Select(i => new Product
        {
            Asin = $"DEMO{i:D6}", Title = $"Demo {job.Query} — {styles[(i - 1) % styles.Length]} model {i:D2}",
            Price = i % 13 == 0 ? null : decimal.Round(14.50m + ((i * 37) % 260) + (i % 4) * 0.25m, 2),
            Rating = i % 11 == 0 ? null : 3.1m + (i * 7 % 20) / 10m,
            ReviewCount = i % 11 == 0 ? null : i * 137 % 8000,
            Url = "", Rank = i
        }).ToList();
        store.Update(job.Id, x => { x.Products = products; x.Status = "enriching"; x.Message = "Preparing the sample dashboard."; });
        foreach (var product in products)
        {
            await Task.Delay(65, token);
            store.Update(job.Id, x =>
            {
                var current = x.Products.First(p => p.Asin == product.Asin);
                current.AvailabilityText = product.Rank % 9 == 0 ? "Currently unavailable."
                    : product.Rank % 5 == 0 ? $"Only {product.Rank % 4 + 1} left in stock - order soon."
                    : product.Rank % 7 == 0 ? "" : "In Stock";
                (current.Availability, current.LimitedStock) = ProductParser.NormalizeAvailability(current.AvailabilityText);
                current.DetailStatus = "complete";
                current.DetailNote = "Illustrative sample; this ASIN does not identify a real Amazon product.";
                x.CompletedDetails++;
                x.Message = $"Prepared {x.CompletedDetails} of {x.Products.Count} demo products.";
            });
        }
        token.ThrowIfCancellationRequested();
        store.Update(job.Id, x => { x.Status = "completed"; x.Message = $"Ready: {x.Products.Count} illustrative products. Switch to live mode after configuring Oxylabs."; });
    }
}
