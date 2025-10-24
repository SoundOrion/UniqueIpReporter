using Serilog.Context;
using System.Collections.Concurrent;
using System.Net;

public class UniqueIpStore
{
    // IP -> 最終観測UTC時刻
    private readonly ConcurrentDictionary<IPAddress, DateTime> _ips = new();
    private readonly ILogger<UniqueIpStore> _logger;

    public UniqueIpStore(ILogger<UniqueIpStore> logger)
    {
        _logger = logger;
    }

    public int Count => _ips.Count;

    /// <summary>
    /// IP を追加 or 最終観測時刻を更新。初回観測ならイベントログを吐く
    /// </summary>
    public bool TryAddOrTouch(IPAddress ip)
    {
        var now = DateTime.UtcNow;

        // 新規追加をまず試みる
        if (_ips.TryAdd(ip, now))
        {
            // ★ 初めて見た IP → イベント発火（ログ）
            using (LogContext.PushProperty("EventType", "IpFirstSeen"))
            {
                _logger.LogInformation("IP first seen {Ip} at {Utc}", ip, now);
            }
            return true;
        }

        // 既存ならタイムスタンプ更新
        _ips[ip] = now;
        return true;
    }

    public bool Contains(IPAddress ip) => _ips.ContainsKey(ip);

    public IEnumerable<IPAddress> GetAllIps() => _ips.Keys;

    public IReadOnlyDictionary<IPAddress, DateTime> Snapshot()
        => _ips.ToDictionary(kv => kv.Key, kv => kv.Value);

    public int CleanupOlderThan(DateTime thresholdUtc)
    {
        var removed = 0;
        foreach (var kv in _ips)
        {
            if (kv.Value < thresholdUtc)
            {
                if (_ips.TryRemove(kv.Key, out _))
                    removed++;
            }
        }
        return removed;
    }

    // --- 件数上限系（オプション）---

    /// <summary>上限を超えていれば、最終観測が古い順に削除して上限まで落とす</summary>
    public int TrimToMaxCount(int maxCount)
    {
        var over = _ips.Count - maxCount;
        if (over <= 0) return 0;

        // ここは一時的に並び替え用スナップショットを作成
        var victims = _ips.ToArray()
                          .OrderBy(kv => kv.Value) // 古い順
                          .Take(over)
                          .Select(kv => kv.Key)
                          .ToList();

        var removed = 0;
        foreach (var ip in victims)
        {
            if (_ips.TryRemove(ip, out _))
                removed++;
        }
        return removed;
    }
}
