using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public class UniqueIpCleanupService : BackgroundService
{
    private readonly UniqueIpStore _store;
    private readonly UniqueIpOptions _opt;

    public UniqueIpCleanupService(UniqueIpStore store, IOptions<UniqueIpOptions> options)
    {
        _store = store;
        _opt = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"[Cleanup] TTL={_opt.EntryTtl}, Interval={_opt.CleanupInterval}");
        while (!stoppingToken.IsCancellationRequested)
        {
            var threshold = DateTime.UtcNow - _opt.EntryTtl;
            var removed = _store.CleanupOlderThan(threshold);
            if (removed > 0)
                Console.WriteLine($"[Cleanup] Removed {removed} old IPs. Remain={_store.Count}");

            try { await Task.Delay(_opt.CleanupInterval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
