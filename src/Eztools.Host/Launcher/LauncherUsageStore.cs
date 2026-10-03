// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eztools.Host.Launcher;

/// <summary>
/// 启动器频次记忆（W7-e，设计方案 §10.11 / §8 D6 复议 = B JSON）。
///
/// <para><b>存什么</b>：<c>&lt;ConfigRoot&gt;/launcher/usage.json</c>，键 = <c>providerId|identity</c>，
/// 值 = 次数 + 最近使用时间。<b>identity 规格见设计方案 §10.11</b>（App ⇒ 目标全路径）；
/// v1 只有 apps 段有消费者（FR-8 收窄：files 排序归索引进程；calc/unit/encode 每轮至多一行，
/// 加成是 no-op）⇒ <b>只记录有消费者的段</b>，记了没人读的数据 = 垃圾数据 + usage.json 无界增长。</para>
///
/// <para><b>线程纪律（NFR-6）</b>：<see cref="Record"/> 由 UI 线程调用 —— 只做内存更新（锁内 O(1)），
/// 落盘走 2s 去抖的后台线程；<see cref="EnsureLoaded"/> 同理 fire-and-forget。
/// <see cref="Flush"/> 是**有界等待**（T-7：退出不得被磁盘 IO 拖住）。</para>
///
/// <para><b>失败语义</b>：任何 IO 异常 ⇒ 记到 <see cref="LastError"/>，**绝不抛** ——
/// 记频次失败不能影响启动应用（§10.11）；调用方（托盘）负责把 LastError 写日志（出口可达，禁静默）。</para>
/// </summary>
public sealed class LauncherUsageStore
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly bool _enabled;

    private Dictionary<string, UsageEntry> _entries = new(StringComparer.Ordinal);
    private bool _loadStarted;
    private string? _lastError;
    private int _pendingWrites;

    /// <summary>
    /// 脏标志：内存与磁盘**可能**不一致（<see cref="Record"/> 置位，快照取走清零，写盘失败置回）。
    /// ★ 没有它，退出 flush 会把"只是加载过"的旧数据无条件重写一遍 —— M4 实机实测踩到：
    /// 关闭前记好 T1，重启托盘（退出 flush 碰文件，mtime 变成退出时刻），关闭期间明明零记录，
    /// 用户看 mtime ≠ T1 误判"关闭没生效"。"碰过文件"必须 = "有过新记录"。
    /// </summary>
    private bool _dirty;

    /// <summary>落盘去抖（设计方案 §10.11：2s）。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>JSON 序列化选项（缩进便于手工检查；时间用本地时区 ISO 格式，与设计方案示例一致）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public LauncherUsageStore(string filePath, bool enabled)
    {
        _filePath = filePath;
        _enabled = enabled;
    }

    /// <summary>总开关（<c>launcher.usage</c>，默认 true）。false ⇒ **完全不读写**（不是"只写不读"）。</summary>
    public bool Enabled => _enabled;

    /// <summary>最近一次 IO 错误（加载/写盘）。调用方负责写日志 —— 出口可达（审查规范 §3.3）。</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>条目数（探针 / selftest 观测用）。</summary>
    public int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>存储键格式：`providerId|identity`（设计方案 §10.11；selftest 钉住格式不被无意改动）。</summary>
    public static string Key(string providerId, string identity) => $"{providerId}|{identity}";

    /// <summary>
    /// 频次加成（**纯函数**，selftest 直测单调性 / 上限 / 地板）：
    /// <c>min(400, 100·log2(1+count)) · decay(daysSinceLastUsed)</c>，
    /// <c>decay(d) = d ≤ 7 ? 1 : max(0.25, 1 − (d−7)/90)</c>（90 天后衰减到 0.25 地板）。
    /// </summary>
    public static double Boost(long count, double lastUsedDays)
    {
        if (count <= 0)
        {
            return 0;
        }

        var baseScore = Math.Min(400, 100 * Math.Log2(1 + count));
        var decay = lastUsedDays <= 7
            ? 1.0
            : Math.Max(0.25, 1.0 - ((lastUsedDays - 7) / 90.0));
        return baseScore * decay;
    }

    /// <summary>
    /// 懒加载（首次调用后**后台线程**读盘，不阻塞调用方 —— NFR-6）。幂等；
    /// 文件缺失 = 正常（空字典）；损坏 ⇒ LastError + 空字典（不阻塞、不抛）。
    /// </summary>
    public void EnsureLoaded()
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_loadStarted)
            {
                return;
            }

            _loadStarted = true;
        }

        _ = Task.Run(LoadNow);
    }

    /// <summary>同步读盘（<see cref="EnsureLoaded"/> 的后台体；selftest 直测重载语义）。</summary>
    public void LoadNow()
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<UsageFile>(json);
            lock (_gate)
            {
                _entries = file?.Entries is { } entries
                    ? new Dictionary<string, UsageEntry>(entries, StringComparer.Ordinal)
                    : new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                // 损坏 ⇒ 空字典继续服务（不阻塞），但**必须留痕**
                _lastError = $"频次文件读取失败（已按空数据处理）：{ex.Message}";
                _entries = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 记录一次使用（count+1，lastUsed=now）。内存即时生效；落盘 2s 去抖后台写。
    /// 线程安全；任何路径都不抛。
    /// </summary>
    public void Record(string providerId, string identity)
    {
        if (!_enabled)
        {
            return;
        }

        var key = Key(providerId, identity);
        lock (_gate)
        {
            _ = _entries.TryGetValue(key, out var entry);
            _entries[key] = new UsageEntry
            {
                Count = (entry?.Count ?? 0) + 1,
                LastUsed = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            };
            _dirty = true;
        }

        ScheduleWrite();
    }

    /// <summary>查加成。未加载 / 未记录 ⇒ 0（懒加载的优雅降级：首查无加成，次查起生效）。</summary>
    public double BoostFor(string providerId, string identity)
    {
        if (!_enabled)
        {
            return 0;
        }

        lock (_gate)
        {
            // ★ 不看 _loaded：Record 是先写内存的（懒加载在后台追），自己记的分必须自己立刻可见 ——
            //   否则"记完立刻查"这一轮拿不到加成（探针真链路实测踩到）。缺键本来就返回 0。
            if (!_entries.TryGetValue(Key(providerId, identity), out var entry))
            {
                return 0;
            }

            var days = (DateTime.Now - DateTime.Parse(entry.LastUsed)).TotalDays;
            return Boost(entry.Count, days);
        }
    }

    /// <summary>观测入口（selftest / 探针直测 count 落盘回读）。</summary>
    public long CountOf(string providerId, string identity)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(providerId, identity), out var entry) ? entry.Count : 0;
        }
    }

    /// <summary>
    /// 有界等待落盘（宿主退出收尾链调用；T-7：≤ 超时即放弃，不让退出被磁盘 IO 拖住）。
    /// 返回是否在时限内完成。
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        if (!_enabled)
        {
            return true;
        }

        // 取走当前脏代次；写盘在调用线程同步做（退出路径等得起 ≤500ms）
        var snapshot = TakeSnapshot();
        return snapshot is null || WriteSnapshot(snapshot, timeout);
    }

    private void ScheduleWrite()
    {
        lock (_gate)
        {
            _pendingWrites++;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce).ConfigureAwait(false);

            lock (_gate)
            {
                _pendingWrites--;
                if (_pendingWrites > 0)
                {
                    return;   // 已有更晚的写入排队，让最后那笔落盘（自然合并）
                }
            }

            var snapshot = TakeSnapshot();
            if (snapshot is not null)
            {
                _ = WriteSnapshot(snapshot, Debounce + TimeSpan.FromSeconds(3));
            }
        });
    }

    /// <summary>
    /// 取快照（**无脏数据 ⇒ null**，见 <see cref="_dirty"/>）。快照 = 深拷贝，写盘不持锁；
    /// 取走即清脏（此后无新 <see cref="Record"/> 就不该再碰文件）。写盘失败由
    /// <see cref="WriteSnapshot"/> 把脏位置回。
    /// </summary>
    private Dictionary<string, UsageEntry>? TakeSnapshot()
    {
        lock (_gate)
        {
            if (!_dirty || _entries.Count == 0)
            {
                return null;
            }

            _dirty = false;
            return new Dictionary<string, UsageEntry>(_entries, StringComparer.Ordinal);
        }
    }

    /// <summary>原子写：临时文件 + 覆盖移动；失败清临时文件 + LastError（设计方案 §11.4 频次文件行）。</summary>
    private bool WriteSnapshot(Dictionary<string, UsageEntry> snapshot, TimeSpan timeout)
    {
        var tmp = _filePath + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }

            var file = new UsageFile { Version = 1, Entries = snapshot };
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception cleanupEx)
            {
                lock (_gate)
                {
                    _lastError = $"频次临时文件清理失败：{cleanupEx.Message}";
                }
            }

            lock (_gate)
            {
                _lastError = $"频次文件写入失败：{ex.Message}";
                _dirty = true;   // 快照已取走但没落盘 ⇒ 内存仍领先磁盘，必须重新置脏（下次 flush 重试）
            }

            return false;
        }
    }

    internal sealed class UsageFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("entries")]
        public Dictionary<string, UsageEntry>? Entries { get; set; }
    }

    internal sealed class UsageEntry
    {
        [JsonPropertyName("count")]
        public long Count { get; set; }

        [JsonPropertyName("lastUsed")]
        public string LastUsed { get; set; } = "";
    }
}
