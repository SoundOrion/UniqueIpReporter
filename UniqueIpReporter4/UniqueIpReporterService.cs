using Microsoft.Extensions.Hosting;

public class UniqueIpReporterService : BackgroundService
{
    private readonly UniqueIpStore _store;

    public UniqueIpReporterService(UniqueIpStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Console.WriteLine($"[Reporter] Unique IPs: {_store.Count}");
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
