using System.Text.Json;
using System.Threading.Channels;

namespace AmazonProductExplorer;

/// <summary>Local JSON persistence, bounded admission, and one in-process work queue.</summary>
public sealed class JobStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ScrapeJob> jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> cancellation = [];
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JobStore(IWebHostEnvironment environment, IConfiguration configuration, ILogger<JobStore> logger)
    {
        directory = configuration["Storage:Directory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "jobs");
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<ScrapeJob>(File.ReadAllText(path), Json);
                if (job is null || !Guid.TryParseExact(job.Id, "N", out _)) continue;
                if (!ScrapeJob.IsTerminal(job.Status))
                {
                    job.Status = "interrupted";
                    job.Message = "The server stopped before this job finished. Start a new job to retry; nothing is automatically resubmitted.";
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    Persist(job);
                }
                jobs[job.Id] = job;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("A saved local job could not be loaded. Its file was left untouched.");
            }
        }
    }

    public ScrapeJob? TryCreate(CreateJobRequest input)
    {
        lock (gate)
        {
            if (jobs.Values.Count(j => !ScrapeJob.IsTerminal(j.Status)) >= 3) return null;
            var job = new ScrapeJob { Query = input.Query!.Trim(), Limit = input.Limit, ZipCode = input.ZipCode!, Mode = input.Mode! };
            if (job.Mode == "demo") job.Warnings.Add("Illustrative demo data only. No products, prices, ratings, stock levels or ASINs were collected from Amazon.");
            Persist(job);
            jobs.Add(job.Id, job);
            cancellation.Add(job.Id, new CancellationTokenSource());
            queue.Writer.TryWrite(job.Id);
            return Clone(job);
        }
    }

    public ScrapeJob? Get(string id)
    {
        lock (gate) return jobs.TryGetValue(id, out var job) ? Clone(job) : null;
    }

    public void Update(string id, Action<ScrapeJob> update)
    {
        lock (gate)
        {
            var job = Clone(jobs[id]);
            update(job);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            Persist(job);
            jobs[id] = job;
        }
    }

    public bool Cancel(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job)) return false;
            if (ScrapeJob.IsTerminal(job.Status)) return true;
            cancellation[id].Cancel();
            Update(id, current =>
            {
                current.CancellationRequested = true;
                current.Message = "Cancellation requested. Already submitted Oxylabs jobs may still run and incur charges.";
            });
            return true;
        }
    }

    public CancellationToken Token(string id)
    {
        lock (gate) return cancellation[id].Token;
    }

    public void Release(string id)
    {
        lock (gate)
        {
            if (cancellation.Remove(id, out var source)) source.Dispose();
        }
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken token) => queue.Reader.ReadAllAsync(token);

    private void Persist(ScrapeJob job)
    {
        var path = Path.Combine(directory, $"{job.Id}.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(job, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static ScrapeJob Clone(ScrapeJob job) => JsonSerializer.Deserialize<ScrapeJob>(JsonSerializer.Serialize(job, Json), Json)!;
}
