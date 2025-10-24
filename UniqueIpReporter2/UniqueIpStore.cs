using System.Collections.Concurrent;
using System.Net;

public class UniqueIpStore
{
    // IP -> 最終観測UTC時刻
    private readonly ConcurrentDictionary<IPAddress, DateTime> _ips = new();

    public int Count => _ips.Count;

    public bool TryAddOrTouch(IPAddress ip)
    {
        // 常に「最終観測時刻」を更新する
        _ips.AddOrUpdate(ip, _ => DateTime.UtcNow, (_, __) => DateTime.UtcNow);
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
