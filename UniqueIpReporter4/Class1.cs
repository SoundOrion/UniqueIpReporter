// .NET 8 / C# 12 サンプル
// 要件:
//  - 数千台への配信を20分ごとに繰り返す
//  - 別BackgroundServiceがIPを ConcurrentDictionary<IP, 更新日時> で管理
//  - 今回はその完全版: 共有ストア、UDP自動発見例、期限切れ掃除、
//    20分ジョブで都度接続・レート制御付き一斉送信、結果をストアに反映

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.RateLimiting;

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

    protected override async Task ExecuteAsync_(CancellationToken stoppingToken)
    {
        const string mcast = "239.0.0.1";
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
        udp.JoinMulticastGroup(IPAddress.Parse(mcast));

        _ = CleanupLoop(stoppingToken);
        _logger.LogInformation("Discovery: v4 mcast {Mcast}:{Port}", mcast, _udpPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                // 認証トークン検証などここで
                var ip = result.RemoteEndPoint.Address;
                var now = DateTime.UtcNow;

                _store.Targets.AddOrUpdate(ip, _ => new TargetInfo(now), (_, old) => old with { LastSeenUtc = now });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discovery receive failed");
                await Task.Delay(1000, stoppingToken);
            }
        }
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
    using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

public static class BurstTcpBroadcaster
{
    /// <summary>
    /// 大量宛先に対する都度接続のブロードキャスト（並列上限＋毎秒接続レート制御）。
    /// </summary>
    public static async Task<IReadOnlyList<SendResult>> BroadcastAsync_(
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

        // 1) 1秒あたり connectPerSecondLimit 個の「接続開始」を許可するトークンバケット
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = connectPerSecondLimit,        // バケット容量（初回バースト許容量）
            TokensPerPeriod = connectPerSecondLimit,   // 毎秒補充トークン数
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = maxConcurrency * 2,           // 待たせる最大件数（環境に合わせて）
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        // 2) 同時実行は maxConcurrency まで
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct };

        await Parallel.ForEachAsync(endpoints, po, async (ep, token) =>
        {
            // レート制御：接続開始トークンを取得
            using var lease = await limiter.AcquireAsync(1, token);
            if (!lease.IsAcquired) return; // キュー超過などで取得失敗

            // 1宛先分の接続→送信
            var r = await SendOnceAsync(ep, payload, connectTimeoutMs, sendTimeoutMs, token);
            results.Add(r);
        });

        return results.ToArray();
    }
}

public static async Task<IReadOnlyList<SendResult>> BroadcastAsync_(
    IEnumerable<IPAddress> ips, int port, ReadOnlyMemory<byte> payload,
    int connectTimeoutMs = 3000, int sendTimeoutMs = 3000,
    int maxConcurrency = 256, int connectPerSecondLimit = 200,
    CancellationToken ct = default)
    {
        var endpoints = ips.Select(ip => new IPEndPoint(ip, port));

        var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = connectPerSecondLimit,
            TokensPerPeriod = connectPerSecondLimit,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = int.MaxValue
        });

        var results = new ConcurrentBag<SendResult>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct };

        await Parallel.ForEachAsync(endpoints, po, async (ep, token) =>
        {
            using var lease = await limiter.AcquireAsync(1, token);
            if (!lease.IsAcquired) return;

            var r = await SendOnceAsync(ep, payload, connectTimeoutMs, sendTimeoutMs, token);
            results.Add(r);
        });

        return results.ToArray();
    }

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

    /// <summary>
    /// レート制御用のトークンバケットを定期的に補充するループ。
    /// </summary>
    /// <param name="sem">接続開始を制御するための <see cref="SemaphoreSlim"/>。現在の残トークン数を保持します。</param>
    /// <param name="limit">1秒あたりに許可する最大接続数。トークンの最大数でもあります。</param>
    /// <param name="ct">キャンセル要求を検出するための <see cref="CancellationToken"/>。</param>
    /// <remarks>
    /// このメソッドは非同期ループとして動作し、1秒ごとにトークンを補充します。
    /// <para>
    /// <list type="bullet">
    /// <item><description><c>SemaphoreSlim.WaitAsync()</c> でトークンを1つ消費します。</description></item>
    /// <item><description><c>TokenRefillLoop</c> が 1 秒ごとにトークンを補充し、最大値 (<paramref name="limit"/>) に戻します。</description></item>
    /// <item><description>結果として、「1秒あたり最大 <paramref name="limit"/> 接続」の滑らかなレート制御が実現します。</description></item>
    /// </list>
    /// </para>
    /// </remarks>
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

            //Span<byte> lenBuf = stackalloc byte[4];
            //BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length);
            //stream.Write(lenBuf);
            //await stream.WriteAsync(payload, ct);
            //await stream.FlushAsync(ct);

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

//そうそう、それ、すごく良い観察です👏

//実は `IPAddress.HostToNetworkOrder()` というメソッドは、**歴史的に「ネットワークはビッグエンディアン」という前提で作られている**からこそ存在するんです。
//そして、「逆方向（NetworkToHostOrder）」もちゃんとあるんですよ！　でも普段あまり見ないだけです。

//---

//## 🔹 実は両方ある

//| 方向           | メソッド                                      | 役割                               |
//| ------------ | ----------------------------------------- | -------------------------------- |
//| ホスト → ネットワーク | `IPAddress.HostToNetworkOrder(int value)` | CPUのネイティブ順（たいていリトル）をビッグエンディアンに変換 |
//| ネットワーク → ホスト | `IPAddress.NetworkToHostOrder(int value)` | ビッグエンディアンをCPUのネイティブ順（リトル）に戻す     |

//両方ちゃんと実装されていて、内部的には同じビット反転処理をしているだけです（CPUがリトルの場合はスワップ、ビッグの場合はそのまま）。

//---

//## 💡 使う場面の典型例

//送信側（クライアントなど）：

//```csharp
//var len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
//await stream.WriteAsync(len);
//```

//受信側（サーバーなど）：

//```csharp
//var lenBuf = new byte[4];
//await stream.ReadExactlyAsync(lenBuf, 0, 4);
//int dataLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
//```

//こうすることで、送信側と受信側のCPUエンディアンが違っても安全に通信できます。

//---

//## 🔍 なぜ「HostToNetworkOrder」が主に使われるか

//* TCP/IPの世界では「ネットワーク上はビッグエンディアン」という標準が**不変**。
//* だから送信時に「ホスト → ネットワーク」に変換するのが**必ず必要**。
//* 一方で「NetworkToHostOrder」は、受信時しか使わないので、コード例には出にくいんです。

//---

//## ✅ まとめ

//| メソッド                   | 意味                     | よく使う側         |
//| ---------------------- | ---------------------- | ------------- |
//| `HostToNetworkOrder()` | ローカルCPUの順をビッグエンディアンに変換 | 送信時（クライアントなど） |
//| `NetworkToHostOrder()` | ビッグエンディアンをローカルCPU順に戻す  | 受信時（サーバーなど）   |

//---

//つまりあなたの言う通り、

//> 「ビッグエンディアンが標準で、それに合わせるための変換メソッド」

//という認識が正しくて、
//しかも「逆方向もあるけど出番が少ない」っていう、地味に歴史の名残りを今でも背負ってるAPIなんです 😊

public static class Framing
{
    /// <summary>
    /// 生データを Deflate 圧縮し、圧縮の方が短ければ「負の長さ + 圧縮データ」、
    /// そうでなければ「正の長さ + 生データ」を Little Endian で送る（非同期版）。
    /// </summary>
    public static async ValueTask SendFrameLEAsync(
        Stream s,
        ReadOnlyMemory<byte> raw,
        CompressionLevel level = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        if (!s.CanWrite) throw new InvalidOperationException("Stream is not writable.");

        // まず常に圧縮を試す（可変長 MemoryStream に圧縮を書き出し）
        int compLen;
        ReadOnlyMemory<byte> compMem;
        var ms = new MemoryStream(capacity: raw.Length);
        await using (ms.ConfigureAwait(false))
        await using (var ds = new DeflateStream(ms, level, leaveOpen: true))
        {
            await ds.WriteAsync(raw, ct).ConfigureAwait(false);
            // FlushAsync は必須ではないが、早期に OS バッファへ流したい場合に有効
            await ds.FlushAsync(ct).ConfigureAwait(false);
        } // DisposeAsync によりフッタも書かれ、ms.Position が確定

        compLen = checked((int)ms.Position);
        if (!ms.TryGetBuffer(out ArraySegment<byte> seg))
        {
            // ほぼ起きないが、念のため ToArray にフォールバック
            compMem = new ReadOnlyMemory<byte>(ms.ToArray(), 0, compLen);
        }
        else
        {
            compMem = new ReadOnlyMemory<byte>(seg.Array!, 0, compLen);
        }

        // どちらを送るか決定
        bool useCompressed = compLen < raw.Length;
        int length = useCompressed ? -compLen : raw.Length;
        ReadOnlyMemory<byte> payload = useCompressed ? compMem : raw;

        // 長さ（LE）を書いてから本体
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBytes, length);

        await s.WriteAsync(lenBytes, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    // 同期APIが必要なら薄いラッパーを用意
    public static void SendFrameLE(Stream s, ReadOnlySpan<byte> raw)
        => SendFrameLEAsync(s, raw.ToArray()).GetAwaiter().GetResult();
}