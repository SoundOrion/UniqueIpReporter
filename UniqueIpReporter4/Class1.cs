// .NET 8 / C# 12 サンプル
// 要件:
//  - 数千台への配信を20分ごとに繰り返す
//  - 別BackgroundServiceがIPを ConcurrentDictionary<IP, 更新日時> で管理
//  - 今回はその完全版: 共有ストア、UDP自動発見例、期限切れ掃除、
//    20分ジョブで都度接続・レート制御付き一斉送信、結果をストアに反映

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#region モデル/ストア
public enum SendStatus { Unknown, Success, Timeout, Refused, Reset, NetworkError, OtherError }

public sealed record TargetInfo(
    DateTime LastSeenUtc,
    SendStatus LastStatus = SendStatus.Unknown,
    string? LastError = null,
    int ConsecutiveFailures = 0
);

public interface ITargetIpStore
{
    ConcurrentDictionary<IPAddress, TargetInfo> Targets { get; }
}

public sealed class TargetIpStore : ITargetIpStore
{
    public ConcurrentDictionary<IPAddress, TargetInfo> Targets { get; } = new();
}
#endregion

#region UDP 自動発見サービス（任意）
// サーバー側が 239.0.0.1:55001（例）へ定期ビーコン or 単純にユニキャストでHELLOを送ってくる想定
// ※ 本番では発見元はDB/CMDBでもOK。このサービスは例として同居させる。
public sealed class TargetDiscoveryService : BackgroundService
{
    private readonly ITargetIpStore _store;
    private readonly ILogger<TargetDiscoveryService> _logger;
    private readonly int _udpPort;
    private readonly TimeSpan _expireAfter;

    public TargetDiscoveryService(ITargetIpStore store, ILogger<TargetDiscoveryService> logger,
        int udpPort = 55001, TimeSpan? expireAfter = null)
    {
        _store = store;
        _logger = logger;
        _udpPort = udpPort;
        _expireAfter = expireAfter ?? TimeSpan.FromHours(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = CleanupLoop(stoppingToken);
        using var udp = new UdpClient(_udpPort);
        _logger.LogInformation("TargetDiscoveryService listening UDP:{Port}", _udpPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var ip = result.RemoteEndPoint.Address;
                var now = DateTime.UtcNow;

                _store.Targets.AddOrUpdate(ip,
                    _ => new TargetInfo(now),
                    (_, old) => old with { LastSeenUtc = now });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discovery receive failed");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var threshold = now - _expireAfter;
                foreach (var kv in _store.Targets.ToArray())
                {
                    if (kv.Value.LastSeenUtc < threshold)
                    {
                        _store.Targets.TryRemove(kv.Key, out _);
                    }
                }
            }
            catch (Exception) { /* ignore */ }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}
#endregion

#region 一斉送信ブロードキャスター（都度接続・レート制御・結果収集）
public sealed record SendResult(IPAddress Ip, SendStatus Status, string? Error = null);

public static class BurstTcpBroadcaster
{
    public static async Task<IReadOnlyList<SendResult>> BroadcastAsync(
        IEnumerable<IPAddress> ips,
        int port,
        ReadOnlyMemory<byte> payload,
        int connectTimeoutMs = 3000,
        int sendTimeoutMs = 3000,
        int maxConcurrency = 256,
        int connectPerSecondLimit = 200,
        CancellationToken ct = default)
    {
        var endpoints = ips.Select(ip => new IPEndPoint(ip, port)).ToArray();
        var results = new ConcurrentBag<SendResult>();

        using var concurrency = new SemaphoreSlim(maxConcurrency);
        using var rate = new SemaphoreSlim(connectPerSecondLimit);
        _ = TokenRefillLoop(rate, connectPerSecondLimit, ct);

        var tasks = endpoints.Select(async ep =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                await rate.WaitAsync(ct); // 接続レート制御
                results.Add(await SendOnceAsync(ep, payload, connectTimeoutMs, sendTimeoutMs, ct));
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.ToArray();
    }

    private static async Task TokenRefillLoop(SemaphoreSlim sem, int limit, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                var deficit = Math.Max(0, limit - sem.CurrentCount);
                for (int i = 0; i < deficit; i++) sem.Release();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private static async Task<SendResult> SendOnceAsync(IPEndPoint ep, ReadOnlyMemory<byte> payload,
        int connectTimeoutMs, int sendTimeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(connectTimeoutMs);
                await client.ConnectAsync(ep.Address, ep.Port, cts.Token);
            }

            using var stream = client.GetStream();
            var len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));

            using (var cts = new CancellationTokenSource(sendTimeoutMs))
            using (ct.Register(cts.Cancel))
            {
                await stream.WriteAsync(len, cts.Token);
                await stream.WriteAsync(payload, cts.Token);
                await stream.FlushAsync(cts.Token);
            }

            return new SendResult(ep.Address, SendStatus.Success);
        }
        catch (OperationCanceledException ex)
        {
            return new SendResult(ep.Address, SendStatus.Timeout, ex.Message);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new SendResult(ep.Address, SendStatus.Refused, ex.Message);
        }
        catch (IOException ex)
        {
            return new SendResult(ep.Address, SendStatus.Reset, ex.Message);
        }
        catch (SocketException ex)
        {
            return new SendResult(ep.Address, SendStatus.NetworkError, ex.Message);
        }
        catch (Exception ex)
        {
            return new SendResult(ep.Address, SendStatus.OtherError, ex.Message);
        }
    }
}
#endregion

#region 20分ジョブ（結果をストアに反映）
public sealed class BroadcastJobService : BackgroundService
{
    private readonly ITargetIpStore _store;
    private readonly ILogger<BroadcastJobService> _logger;
    private readonly int _port;

    // チューニング項目
    private readonly int _connectTimeoutMs = 3000;
    private readonly int _sendTimeoutMs = 3000;
    private readonly int _maxConcurrency = 256;
    private readonly int _connectPerSecondLimit = 200;

    public BroadcastJobService(ITargetIpStore store, ILogger<BroadcastJobService> logger, int port = 5000)
    {
        _store = store;
        _logger = logger;
        _port = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 初回ジッター（全台同時起動を避ける）
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(0, 120)), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(20));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Broadcast run failed");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var ips = _store.Targets.Keys.ToArray();
        if (ips.Length == 0)
        {
            _logger.LogWarning("No active targets");
            return;
        }

        var payload = Encoding.UTF8.GetBytes($"hello @ {DateTime.UtcNow:o}"); // 実際のペイロードに差し替え

        _logger.LogInformation("Broadcast start: {Count} targets", ips.Length);
        var results = await BurstTcpBroadcaster.BroadcastAsync(
            ips, _port, payload, _connectTimeoutMs, _sendTimeoutMs, _maxConcurrency, _connectPerSecondLimit, ct);

        // 結果をストアに反映
        foreach (var r in results)
        {
            _store.Targets.AddOrUpdate(r.Ip,
                _ => new TargetInfo(DateTime.UtcNow, r.Status, r.Error, r.Status == SendStatus.Success ? 0 : 1),
                (_, old) => old with
                {
                    LastSeenUtc = old.LastSeenUtc, // 発見時刻はそのまま（発見サービスが更新）
                    LastStatus = r.Status,
                    LastError = r.Error,
                    ConsecutiveFailures = r.Status == SendStatus.Success ? 0 : (old.ConsecutiveFailures + 1)
                });
        }

        // 失敗率などメトリクス
        var total = results.Count;
        var success = results.Count(x => x.Status == SendStatus.Success);
        var fail = total - success;
        _logger.LogInformation("Broadcast done. Success={Success}, Fail={Fail}", success, fail);
    }
}
#endregion

#region Program.cs 登録例
// var builder = Host.CreateApplicationBuilder(args);
// builder.Services.AddSingleton<ITargetIpStore, TargetIpStore>();
// builder.Services.AddHostedService<TargetDiscoveryService>(); // DB等ならこれを別のサービスに置換
// builder.Services.AddHostedService(sp =>
// {
//     var store = sp.GetRequiredService<ITargetIpStore>();
//     var logger = sp.GetRequiredService<ILogger<BroadcastJobService>>();
//     return new BroadcastJobService(store, logger, port: 5000);
// });
// await builder.Build().RunAsync();
#endregion
