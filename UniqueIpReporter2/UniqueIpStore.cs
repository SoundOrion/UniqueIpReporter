using System.Collections.Concurrent;
using System.Net;

public class UniqueIpStore
{
    // IP -> �ŏI�ϑ�UTC����
    private readonly ConcurrentDictionary<IPAddress, DateTime> _ips = new();

    public int Count => _ips.Count;

    public bool TryAddOrTouch(IPAddress ip)
    {
        // ��Ɂu�ŏI�ϑ������v���X�V����
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

    // --- ��������n�i�I�v�V�����j---

    /// <summary>����𒴂��Ă���΁A�ŏI�ϑ����Â����ɍ폜���ď���܂ŗ��Ƃ�</summary>
    public int TrimToMaxCount(int maxCount)
    {
        var over = _ips.Count - maxCount;
        if (over <= 0) return 0;

        // �����͈ꎞ�I�ɕ��ёւ��p�X�i�b�v�V���b�g���쐬
        var victims = _ips.ToArray()
                          .OrderBy(kv => kv.Value) // �Â���
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
