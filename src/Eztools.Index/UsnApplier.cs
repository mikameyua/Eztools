// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Index;

/// <summary>
/// USN 变更 → 索引的应用器（W3-c-2）。按**批次内顺序逐条**应用（USN 严格有序，设计方案 §4.4），
/// 同一 FRN 折叠：RENAME_OLD 先删并记 pending，同 FRN 的 RENAME_NEW 到达时 upsert 并丢弃 pending。
///
/// 语义表（§2.3 修正版）：
///   FILE_CREATE / RENAME_NEW_NAME / DATA_EXTEND → upsert（幂等：名字与父没变就零成本跳过）；
///   FILE_DELETE / RENAME_OLD_NAME → remove by FRN；OLD 另记 pending（配对簿记 + TTL 清理）。
/// 幂等性（重启重放安全）：同批记录重复应用结果不变 —— upsert 是覆盖、delete 是按 FRN 反查。
/// </summary>
public sealed class UsnApplier
{
    /// <summary>pending 配对超时（跨批 rename 的 NEW 一直不来 ⇒ 簿记过期清掉，防无限增长；W3-c-2 风险行）。</summary>
    public int PendingTtlMs { get; init; } = 30_000;

    /// <summary>时钟注入（selftest 控制 TTL 到期；null = Environment.TickCount64）。</summary>
    public Func<long>? Clock { get; init; }

    private readonly Dictionary<ulong, long> _pendingRename = new();

    // 计数（观测面，缺一不可断言——VolumeWorker 对账三计数同纪律）
    public long Upserts { get; private set; }
    public long Deletes { get; private set; }
    public long RenameFolds { get; private set; }
    public long PendingExpired { get; private set; }

    /// <summary>当前 pending 配对数（rename OLD 已见、NEW 未到）。</summary>
    public int PendingCount => _pendingRename.Count;

    public void Apply(IReadOnlyList<UsnRecord> batch, IndexStore store)
    {
        long now = Now();
        ExpirePending(now);

        foreach (var rec in batch)
        {
            // NEW 先于 OLD 判：一条记录同现两位（理论边界）时新名优先（upsert 覆盖一切）
            if (rec.Has(UsnReason.RenameNewName) || rec.Has(UsnReason.FileCreate) || rec.Has(UsnReason.DataExtend))
            {
                Upsert(rec, store);
                if (_pendingRename.Remove(rec.Frn))
                {
                    RenameFolds++; // 同 FRN 的 OLD 已在 pending ⇒ 折叠（丢弃配对）
                }
            }

            if (rec.Has(UsnReason.FileDelete) || rec.Has(UsnReason.RenameOldName))
            {
                if (store.Remove(rec.Frn))
                {
                    Deletes++;
                }

                if (rec.Has(UsnReason.RenameOldName))
                {
                    // 只簿记不阻塞：NEW 不来（上游异常）也只是 pending 挂到 TTL；
                    // 条目已在上一行删除 ⇒ 旧名立即不可搜（24.5 反向断言的依据）
                    _pendingRename[rec.Frn] = now;
                }
            }
        }
    }

    private void Upsert(in UsnRecord rec, IndexStore store)
    {
        // 幂等快路径：条目已存在且 parent/name 未变（DATA_EXTEND 之类的纯内容位）⇒ 零堆增长跳过
        if (store.TryGet(rec.Frn, out var existing)
            && existing.ParentFrn == rec.ParentFrn && existing.Name == rec.Name)
        {
            Upserts++;
            return;
        }

        // 覆盖语义 = Remove + TryAdd（堆旧字节成死区，Compact 按碎片率回收——既有契约）
        store.Remove(rec.Frn);
        if (store.TryAdd(rec.Frn, rec.ParentFrn, rec.Name, UsnRecordParser.MapFlags(rec.FileAttributes)))
        {
            Upserts++;
        }
    }

    private void ExpirePending(long now)
    {
        if (_pendingRename.Count == 0)
        {
            return;
        }

        List<ulong>? expired = null;
        foreach (var (frn, at) in _pendingRename)
        {
            if (now - at > PendingTtlMs)
            {
                (expired ??= new List<ulong>()).Add(frn);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var frn in expired)
        {
            _pendingRename.Remove(frn);
            PendingExpired++;
        }
    }

    private long Now() => Clock is { } clock ? clock() : Environment.TickCount64;
}
