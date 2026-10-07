// ============================================================================
// Eztools.Index / VolumeWorker —— W3-a-3 全量枚举（复用 volume.readMft）
//
// 职责（实施计划 W3-a-3）：把整卷 MFT 记录灌进 IndexStore。
//   - **复用不改** `volume.readMft`（FSCTL_ENUM_USN_DATA，Eztools.Core 特权层）；
//     本类只做"游标续传循环 + 对账 + 多卷编排"，不碰 Win32。
//   - 卷间并行、**卷内单任务**（IndexStore 单线程契约，§3.6）。
//   - 每卷一个 IndexStore（持久化 = per-volume `.ezidx`，§3.4；FRN 只在卷内唯一）。
//
// 读取注入（可测试性，S9/S14 纪律）：
//   <see cref="MftReader"/> 委托隔离了"从哪拿 readMft 响应"。真实适配（宿主 → Core
//   的 primitive.execute，PrimitiveException → MftReaderException 翻译）随 W3-b
//   宿主消费 search.index 时接线；selftest 注入假 reader，零进程、零提权可验。
//
// 游标语义（**既有修正语义，不要改** —— P3 §5 / VolumePrimitives.ParseRecords）：
//   截断批 cursor = 该批最后一条已返回记录的 FRN，下一批从它重放首条；
//   本类按**内容**消化重放（同 FRN + 同 parent + 同 name ⇒ 不算重复、不算更新）。
//
// 内部错误码（-32019/-32020，续接 -32013..-32018 的扩展区间，登记于设计方案 §2.4）：
//   -32019 cursor-stalled（游标不推进且 done=false —— 真实流不可能，防御夹具/系统 quirk 死循环）
//   -32020 bad-batch-shape（readMft 响应缺字段/类型错 —— 契约被破坏时 fail-fast，不静默吞）
//
// 已知边界（显式登记，不做静默降级）：
//   - readMft 记录无 attributes ⇒ flags 一律 0（目录/隐藏判定随 W3-c 变更流或扩展原语）。
//   - readMft 不返回 USN journal id / nextUsn ⇒ 增量起点延后至 W3-c（USN 读取线程）；
//     W3-a-4 持久化 header 的 nextUsn 字段本阶段写 0。
// ============================================================================

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Eztools.Index;

/// <summary>
/// readMft 读取委托：返回 JSON-RPC result 节点（`volume.readMft` 响应：
/// `{volume, cursor, done, count, records:[{frn,parent,name}]}`）。
/// </summary>
public delegate JsonNode MftReader(string volume, long? cursor, int maxRecords);

/// <summary>读取失败（含 Core 侧结构化错误的翻译形态）。 <see cref="ErrorCode"/> 供断言落字面量。</summary>
public sealed class MftReaderException : Exception
{
    public MftReaderException(int errorCode, string message) : base(message) => ErrorCode = errorCode;

    public int ErrorCode { get; }
}

/// <summary>
/// 一卷的枚举目标：卷盘符 + 该卷自己的 IndexStore + **自有子树作用域**（P4）
/// + **用户排除作用域**（W11-a）+ **用户限定作用域**（W11-b，`pathFilter`）。
/// <see cref="Own"/> 为 null = 不排除任何条目（与"未锚定的 <see cref="OwnScope"/>"同效，
/// 两者都走 <c>Contains</c> 的恒 false 分支）。<see cref="Excluded"/> / <see cref="Include"/> 同理。
///
/// <para>★ <see cref="Include"/> 的判据方向与排除**相反**（"没命中的踢出去"）；
/// "已锚定的空作用域"（<see cref="FrScope.EmptyAnchored"/>）表示"限定生效但本卷不在限定范围
/// ⇒ 整卷不产出"（跨卷语义）。两个实例两个语义，禁止混用（W11 §4.5）。</para>
/// </summary>
public readonly record struct VolumeTarget(
    string Volume, IndexStore Store, OwnScope? Own = null, FrScope? Excluded = null, FrScope? Include = null);

/// <summary>单卷枚举结果报告（全部落具体数字，供日志与断言）。</summary>
public sealed record VolumeIndexReport(
    string Volume,
    long Count,
    int Batches,
    long CursorEnd,
    bool Done,
    long Replays,
    long Updates,
    bool Sorted,
    double ElapsedMs,
    string? Error = null,
    int? ErrorCode = null);

public static class VolumeWorker
{
    /// <summary>每批条数 = readMft 硬上限（VolumePrimitives.HardMaxRecords）。≥ 2 才放得下重放消化。</summary>
    public const int BatchSize = 5000;

    /// <summary>防御上限：游标停滞/夹具 bug 时强行终止（5000 × 100 万 = 50 亿条，远超任何卷）。</summary>
    public const int MaxBatches = 1_000_000;

    /// <summary>游标不推进且未 done —— 真实 readMft 语义下不可能，触发即报错（不静默吞、不死循环）。</summary>
    public const int CursorStalled = -32019;

    /// <summary>readMft 响应缺字段/类型错 —— 契约破坏 fail-fast。</summary>
    public const int BadBatchShape = -32020;

    /// <summary>
    /// 卷排序（实施计划 W3-a-3 ③）：归一化盘符 → <c>C:</c> 优先（系统卷）→ 其余字母序 → 去重（大小写不敏感）。
    /// 接受 "C" / "C:" / "c:\" / "\\.\C:" 形态，统一输出 <c>"C:"</c>。
    /// </summary>
    public static IReadOnlyList<string> OrderVolumes(IEnumerable<string> volumes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var raw in volumes)
        {
            var letter = ExtractLetter(raw);
            if (letter is null || !seen.Add(letter.ToString()))
            {
                continue;
            }

            ordered.Add(letter + ":");
        }

        // C: 置顶，其余字母序（稳定：C: 恰是字母序第一个时不移动）
        ordered.Sort((a, b) => (a[0] == 'C' ? -1 : a[0]).CompareTo(b[0] == 'C' ? -1 : b[0]));
        return ordered;
    }

    /// <summary>枚举一卷：游标续传循环（≤ <see cref="BatchSize"/> 条/批）灌入 store。</summary>
    public static async Task<VolumeIndexReport> EnumerateVolumeAsync(
        IndexStore store, string volume, MftReader reader, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        long count = 0, replays = 0, updates = 0;
        int batches = 0;
        long? cursor = null;
        long cursorEnd = 0;
        var done = false;

        try
        {
            while (!done)
            {
                ct.ThrowIfCancellationRequested();
                if (batches >= MaxBatches)
                {
                    throw new MftReaderException(CursorStalled,
                        $"卷 {volume} 批数达到防御上限 {MaxBatches} 而未完成（游标停滞或读取实现异常）");
                }

                var node = reader(volume, cursor, BatchSize)
                    ?? throw new MftReaderException(BadBatchShape, $"卷 {volume} 第 {batches + 1} 批返回 null");

                var doneNode = node["done"]
                    ?? throw new MftReaderException(BadBatchShape, $"卷 {volume} 第 {batches + 1} 批缺 done 字段（契约破坏，不许静默当 false）");
                done = doneNode.GetValue<bool>();
                var next = node["cursor"]?.GetValue<long?>();
                var records = node["records"] as JsonArray
                    ?? throw new MftReaderException(BadBatchShape, $"卷 {volume} 第 {batches + 1} 批缺 records 数组");

                if (!done)
                {
                    // 游标守卫（2026-09-24 修正，真卷实测教训）：cursor 是**不透明续传令牌**——
                    // 截断批 = 最后一条记录的完整 FRN（含序列号位）；整批消费 = 系统缓冲区边界。
                    // 两种格式切换时数值可以合法变小，**不能做数值单调比较**（旧守卫 next <= prev
                    // 即抛 -32019，真卷上会在"截断批 → 整批"切换点误杀，探针实测抓到）。
                    // 停滞防御改为行为判据：未 done 却缺 cursor 或零记录 ⇒ 无法推进（真实
                    // readMft 不产生空批）；极端异常由 MaxBatches 兜底。
                    if (next is null || records.Count == 0)
                    {
                        throw new MftReaderException(CursorStalled,
                            $"卷 {volume} 游标停滞（未 done 却缺游标或零记录；prev={cursor?.ToString() ?? "<null>"}, next={next?.ToString() ?? "<null>"}）");
                    }
                }

                foreach (var rec in records)
                {
                    var frn = unchecked((ulong)(rec?["frn"]?.GetValue<long>()
                        ?? throw new MftReaderException(BadBatchShape, $"卷 {volume} 第 {batches + 1} 批记录缺 frn")));
                    var parent = unchecked((ulong)(rec["parent"]?.GetValue<long>()
                        ?? throw new MftReaderException(BadBatchShape, $"卷 {volume} 第 {batches + 1} 批记录缺 parent")));
                    var name = rec["name"]?.GetValue<string>()
                        ?? throw new MftReaderException(BadBatchShape, $"卷 {volume} 第 {batches + 1} 批记录缺 name");

                    if (store.TryAdd(frn, parent, name, flags: 0))
                    {
                        count++;
                    }
                    else if (store.TryGet(frn, out var existing)
                        && existing.ParentFrn == parent && string.Equals(existing.Name, name, StringComparison.Ordinal))
                    {
                        // 重放消化（P3 §5 修正语义）：上一批最后一条已返回记录，内容相同 ⇒ 正常，不计重复
                        replays++;
                    }
                    else if (store.Remove(frn) && store.TryAdd(frn, parent, name, flags: 0))
                    {
                        // 同 FRN 异内容 = 记录号被复用（文件删除重建）⇒ 更新为新一代内容
                        updates++;
                        count++;
                    }
                    else
                    {
                        // 不可达兜底：Remove 成功但 Add 又失败（如名字超长）—— 显式失败，不静默
                        throw new MftReaderException(BadBatchShape,
                            $"卷 {volume} 记录 frn={frn} 既非重放也无法更新（名字超长？）");
                    }
                }

                batches++;
                cursor = next;
                if (next is { } n)
                {
                    cursorEnd = n;
                }
            }

            // I1 契约：批量灌入完成后必须全量校验升序（设计方案 §3.6）
            return new VolumeIndexReport(volume, count, batches, cursorEnd, done,
                replays, updates, store.ValidateSorted(), sw.ElapsedMilliseconds);
        }
        catch (MftReaderException ex)
        {
            return new VolumeIndexReport(volume, count, batches, cursorEnd, false,
                replays, updates, store.ValidateSorted(), sw.ElapsedMilliseconds, ex.Message, ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            return new VolumeIndexReport(volume, count, batches, cursorEnd, false,
                replays, updates, store.ValidateSorted(), sw.ElapsedMilliseconds, "已取消", null);
        }
    }

    /// <summary>枚举多卷：**卷间并行、卷内单任务**（实施计划 W3-a-3 ④）。每卷必须用自己的 store。</summary>
    public static async Task<IReadOnlyList<VolumeIndexReport>> EnumerateVolumesAsync(
        IReadOnlyList<VolumeTarget> targets, MftReader reader, CancellationToken ct = default)
    {
        var tasks = new Task<VolumeIndexReport>[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            tasks[i] = EnumerateVolumeAsync(target.Store, target.Volume, reader, ct);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static char? ExtractLetter(string volume)
    {
        var value = volume.Trim().TrimEnd('\\', '/');
        if (value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        value = value.TrimEnd(':');
        return value.Length == 1 && char.IsAsciiLetter(value[0])
            ? char.ToUpperInvariant(value[0])
            : null;
    }
}
