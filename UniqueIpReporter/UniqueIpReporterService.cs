using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

public class UniqueIpReporterService : BackgroundService
{
    private readonly ConcurrentDictionary<IPAddress, byte> _uniqueIps;

    public UniqueIpReporterService(ConcurrentDictionary<IPAddress, byte> uniqueIps)
    {
        _uniqueIps = uniqueIps;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Console.WriteLine($"現在の接続元IP数: {_uniqueIps.Count}");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
