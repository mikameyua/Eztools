// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.ClipboardLib;
using Eztools.Host;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt clip</c> 子命令（W5-a：剪贴板历史库原语层，W5-剪贴板-设计方案.md §7）。
///
/// <list type="bullet">
/// <item><c>ezt clip capture</c> —— 读一次当前系统剪贴板入库（调试/探针出口；常驻监听在 W5-c 接线）。</item>
/// <item><c>ezt clip list / search / pin / unpin / delete / copy / clear / status</c> —— 历史管理。</item>
/// </list>
///
/// <para>退出码契约：0 成功 · 4 条目不存在 · 64 用法错误 · 1 捕获/写入失败。</para>
/// <para>数据落位：<c>&lt;config-root&gt;/data/clip/clips.db</c> + <c>images/</c>
/// （用户数据区，与程序目录分离——设计 §3.4）。</para>
/// </summary>
internal static class ClipCommand
{
    public static Task<int> RunAsync(CliArgs cli)
    {
        ConsoleUi.JsonMode = cli.GetBool("json");

        var sub = cli.Rest.Count > 0 ? cli.Rest[0] : "help";
        return sub switch
        {
            "capture" => CaptureAsync(cli),
            "list" => ListAsync(cli),
            "search" => SearchAsync(cli),
            "pin" => PinAsync(cli, true),
            "unpin" => PinAsync(cli, false),
            "delete" => DeleteAsync(cli),
            "copy" => CopyAsync(cli),
            "clear" => ClearAsync(cli),
            "status" => StatusAsync(cli),
            "help" or "--help" or "-h" => Task.FromResult(Help()),
            _ => Task.FromResult(UnknownSub(sub)),
        };
    }

    private static int UnknownSub(string sub)
    {
        ConsoleUi.Error($"未知子命令: clip {sub}（可用：capture / list / search / pin / unpin / delete / copy / clear / status）");
        return 64;
    }

    private static int Help()
    {
        ConsoleUi.Header("ezt clip —— 剪贴板历史库（W5-a）");
        ConsoleUi.Info("  clip capture             读一次当前剪贴板入库（--blacklist a.exe;b.exe 试黑名单；--json → saved/isNew/id/copyCount）");
        ConsoleUi.Info("  clip list                历史列表（--kind text|image|filelist；--limit N 默认 50；--json）");
        ConsoleUi.Info("  clip search <关键词>     搜索（≥3 字符走 FTS trigram，<3 字符走 LIKE 兜底；--limit N；--json）");
        ConsoleUi.Info("  clip pin|unpin <id>      置顶 / 取消置顶（pinned 不被任何清理删除）");
        ConsoleUi.Info("  clip delete <id>         单条删除（图片条目连带删盘上文件）");
        ConsoleUi.Info("  clip copy <id>           重新复制回系统剪贴板（W5-a 仅文本条目）");
        ConsoleUi.Info("  clip clear [--keep-pinned]  清空历史（--keep-pinned 保留置顶条目）");
        ConsoleUi.Info("  clip status              库状态：总数 / 分类型 / 置顶数 / 库与图片体积（--json 可断言）");
        ConsoleUi.Info("  退出码: 0 成功 · 4 条目不存在 · 64 用法错误 · 1 捕获失败");
        ConsoleUi.Info("  库位置: <config-root>/data/clip/clips.db（--config-root 可覆盖）");
        return 0;
    }

    // ── 共用：库定位 ──
    // config-root 是 EztoolsPaths 的权威（--config-root 可覆盖）；
    // 剪贴板数据在其下 data/clip/——用户数据区，与程序目录分离（卸载程序不丢历史）。

    private static HistoryStore OpenStore(CliArgs cli, out string clipRoot)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        clipRoot = Path.Combine(paths.ConfigRoot, "data", "clip");
        return new HistoryStore(
            Path.Combine(clipRoot, "clips.db"),
            Path.Combine(clipRoot, "images"));
    }

    private static long ParseId(string? raw)
    {
        return long.TryParse(raw, out var id) ? id : -1;
    }

    private static bool TryParseKind(string? raw, out ClipKind kind)
    {
        kind = ClipKind.Text;
        switch (raw?.ToLowerInvariant())
        {
            case null: return false;
            case "text": kind = ClipKind.Text; return true;
            case "image": kind = ClipKind.Image; return true;
            case "file" or "filelist": kind = ClipKind.FileList; return true;
            default: return false;
        }
    }

    private static JsonObject ToJson(ClipEntry e) => new()
    {
        ["id"] = e.Id,
        ["kind"] = e.Kind.ToString().ToLowerInvariant(),
        ["preview"] = e.Preview,
        ["copyCount"] = e.CopyCount,
        ["pinned"] = e.Pinned,
        ["sourceApp"] = e.SourceApp,
        ["imagePath"] = e.ImagePath,
        ["lastUsedAt"] = e.LastUsedAt.ToString("yyyy-MM-dd HH:mm:ss"),
    };

    // ── capture ──

    private static Task<int> CaptureAsync(CliArgs cli)
    {
        using var store = OpenStore(cli, out var clipRoot);

        var filter = new PrivacyFilter();
        if (cli.Get("blacklist") is { Length: > 0 } blacklist)
        {
            filter.SetBlacklist(blacklist.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var (snapshot, error) = ClipboardReader.ReadCurrent();
        if (error is not null)
        {
            ConsoleUi.Error($"读取剪贴板失败: {error}");
            return Task.FromResult(1);
        }

        var decision = filter.Evaluate(snapshot.OwnerProcess);
        if (decision == PrivacyDecision.Blocked)
        {
            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["saved"] = false,
                    ["reason"] = "blacklisted",
                    ["sourceApp"] = snapshot.OwnerProcess,
                }, cli.GetBool("compact"));
            }
            else
            {
                ConsoleUi.Warn($"黑名单命中，未保存（来源: {snapshot.OwnerProcess ?? "?"}）");
            }

            return Task.FromResult(0);
        }

        // UIPI（设计 §8 R1，09-28 修正）：owner 提权 ⇒ 记占位条目（FR-11③）。
        // 原"读不到内容才占位"在默认 UIPI 下对 high IL 源不可达，改为"提权即占位"。
        ClipEntry? draft;
        if (snapshot.OwnerProcess is not null && snapshot.OwnerElevated)
        {
            draft = CaptureService.FromPlaceholder(snapshot.OwnerProcess);
        }
        else
        {
            draft = CaptureService.FromSnapshot(snapshot);
        }

        if (draft is null)
        {
            if (cli.GetBool("json"))
            {
                ConsoleUi.PrintJson(new JsonObject
                {
                    ["saved"] = false,
                    ["reason"] = "empty",
                    ["sourceApp"] = snapshot.OwnerProcess,
                }, cli.GetBool("compact"));
            }
            else
            {
                ConsoleUi.Info("剪贴板没有可捕获的内容（空 / 非文本 / 非文件列表）");
            }

            return Task.FromResult(0);
        }

        var result = store.Upsert(draft);

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["saved"] = true,
                ["isNew"] = result.IsNew,
                ["id"] = result.Entry.Id,
                ["kind"] = result.Entry.Kind.ToString().ToLowerInvariant(),
                ["copyCount"] = result.Entry.CopyCount,
                ["preview"] = result.Entry.Preview,
                ["sourceApp"] = result.Entry.SourceApp,
                ["clipRoot"] = clipRoot,
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Ok(result.IsNew
                ? $"已入库 #{result.Entry.Id}（{result.Entry.Kind}）: {result.Entry.Preview}"
                : $"重复内容，已置顶 #{result.Entry.Id}（第 {result.Entry.CopyCount} 次复制）");
        }

        return Task.FromResult(0);
    }

    // ── list ──

    private static Task<int> ListAsync(CliArgs cli)
    {
        ClipKind? filter = null;
        if (cli.Get("kind") is { } kindArg)
        {
            if (!TryParseKind(kindArg, out var parsed))
            {
                ConsoleUi.Error($"--kind 只认 text / image / filelist: {kindArg}");
                return Task.FromResult(64);
            }

            filter = parsed;
        }

        var limit = ParseLimit(cli, 50);

        using var store = OpenStore(cli, out _);
        var entries = store.List(filter, limit);

        if (cli.GetBool("json"))
        {
            var arr = new JsonArray();
            foreach (var entry in entries)
            {
                arr.Add(ToJson(entry));
            }

            ConsoleUi.PrintJson(arr, cli.GetBool("compact"));
            return Task.FromResult(0);
        }

        ConsoleUi.Header($"剪贴板历史（{entries.Count} 条）");
        ConsoleUi.Table(
            new[] { "ID", "类型", "置顶", "次数", "来源", "摘要" },
            entries.Select(e => new[]
            {
                e.Id.ToString(),
                e.Kind.ToString(),
                e.Pinned ? "📌" : "",
                e.CopyCount.ToString(),
                e.SourceApp ?? "-",
                e.Preview,
            }).ToList());
        return Task.FromResult(0);
    }

    // ── search ──

    private static Task<int> SearchAsync(CliArgs cli)
    {
        var query = cli.Rest.Count > 1 ? cli.Rest[1] : cli.Get("text");
        if (string.IsNullOrWhiteSpace(query))
        {
            ConsoleUi.Error("用法: ezt clip search <关键词> [--limit N] [--json]");
            return Task.FromResult(64);
        }

        var limit = ParseLimit(cli, 50);
        using var store = OpenStore(cli, out _);
        var entries = store.Search(query, limit);

        if (cli.GetBool("json"))
        {
            var arr = new JsonArray();
            foreach (var entry in entries)
            {
                arr.Add(ToJson(entry));
            }

            ConsoleUi.PrintJson(new JsonObject
            {
                ["query"] = query,
                ["hits"] = entries.Count,
                ["items"] = arr,
            }, cli.GetBool("compact"));
            return Task.FromResult(0);
        }

        ConsoleUi.Header($"搜索「{query}」（{entries.Count} 条命中）");
        ConsoleUi.Table(
            new[] { "ID", "类型", "置顶", "次数", "摘要" },
            entries.Select(e => new[]
            {
                e.Id.ToString(),
                e.Kind.ToString(),
                e.Pinned ? "📌" : "",
                e.CopyCount.ToString(),
                e.Preview,
            }).ToList());
        return Task.FromResult(0);
    }

    // ── pin / unpin ──

    private static Task<int> PinAsync(CliArgs cli, bool pinned)
    {
        var id = ParseId(cli.Rest.Count > 1 ? cli.Rest[1] : null);
        if (id < 0)
        {
            ConsoleUi.Error($"用法: ezt clip {(pinned ? "pin" : "unpin")} <id>");
            return Task.FromResult(64);
        }

        using var store = OpenStore(cli, out _);
        if (!store.SetPinned(id, pinned))
        {
            ConsoleUi.Error($"条目不存在: #{id}");
            return Task.FromResult(4);
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["id"] = id,
                ["pinned"] = pinned,
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Ok($"#{id} 已{(pinned ? "置顶" : "取消置顶")}");
        }

        return Task.FromResult(0);
    }

    // ── delete ──

    private static Task<int> DeleteAsync(CliArgs cli)
    {
        var id = ParseId(cli.Rest.Count > 1 ? cli.Rest[1] : null);
        if (id < 0)
        {
            ConsoleUi.Error("用法: ezt clip delete <id>");
            return Task.FromResult(64);
        }

        using var store = OpenStore(cli, out _);
        if (!store.Delete(id))
        {
            ConsoleUi.Error($"条目不存在: #{id}");
            return Task.FromResult(4);
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject { ["deleted"] = id }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Ok($"#{id} 已删除");
        }

        return Task.FromResult(0);
    }

    // ── copy ──

    private static Task<int> CopyAsync(CliArgs cli)
    {
        var id = ParseId(cli.Rest.Count > 1 ? cli.Rest[1] : null);
        if (id < 0)
        {
            ConsoleUi.Error("用法: ezt clip copy <id>");
            return Task.FromResult(64);
        }

        using var store = OpenStore(cli, out _);
        var entry = store.GetById(id);
        if (entry is null)
        {
            ConsoleUi.Error($"条目不存在: #{id}");
            return Task.FromResult(4);
        }

        if (entry.Kind != ClipKind.Text || entry.Content is null)
        {
            // W5-b 面板带图片/文件条目的回复制（PNG 解码 + FileDrop）。
            ConsoleUi.Error($"#{id} 是 {entry.Kind} 条目：W5-a 仅支持文本条目回复制");
            return Task.FromResult(64);
        }

        var error = ClipboardReader.CopyText(entry.Content);
        if (error is not null)
        {
            ConsoleUi.Error($"写剪贴板失败: {error}");
            return Task.FromResult(1);
        }

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject { ["copied"] = id }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Ok($"#{id} 已复制回剪贴板");
        }

        return Task.FromResult(0);
    }

    // ── clear ──

    private static Task<int> ClearAsync(CliArgs cli)
    {
        var keepPinned = cli.GetBool("keep-pinned");
        using var store = OpenStore(cli, out _);
        var deleted = store.Clear(keepPinned);

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["deleted"] = deleted,
                ["keepPinned"] = keepPinned,
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Ok($"已删除 {deleted} 条" + (keepPinned ? "（置顶条目已保留）" : ""));
        }

        return Task.FromResult(0);
    }

    // ── status ──

    private static Task<int> StatusAsync(CliArgs cli)
    {
        using var store = OpenStore(cli, out var clipRoot);
        var s = store.Status();

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["clipRoot"] = clipRoot,
                ["total"] = s.Total,
                ["text"] = s.TextCount,
                ["image"] = s.ImageCount,
                ["fileList"] = s.FileListCount,
                ["pinned"] = s.Pinned,
                ["dbBytes"] = s.DbBytes,
                ["imageFiles"] = s.ImageFiles,
                ["imageBytes"] = s.ImageBytes,
                ["oldest"] = s.Oldest,
                ["newest"] = s.Newest,
                // 如实呈现职责划分：监听由常驻托盘进程负责（消息驱动），CLI 是一次性进程只读库。
                // CLI 无法探测托盘是否在跑（跨进程无契约），所以不假装报"运行中"。
                ["monitor"] = "delegated-to-host",
                ["monitorNote"] = "剪贴板监听由 Eztools.Desktop 托盘进程提供（W5-c）；托盘 selfcheck 可见运行态",
            }, cli.GetBool("compact"));
            return Task.FromResult(0);
        }

        ConsoleUi.Header("剪贴板历史库");
        ConsoleUi.Field("库位置", clipRoot);
        ConsoleUi.Field("总条数", $"{s.Total}（文本 {s.TextCount} / 图片 {s.ImageCount} / 文件 {s.FileListCount}）");
        ConsoleUi.Field("置顶", s.Pinned.ToString());
        ConsoleUi.Field("库体积", $"{FormatBytes(s.DbBytes)}（图片 {s.ImageFiles} 个 / {FormatBytes(s.ImageBytes)}）");
        ConsoleUi.Field("时间范围", $"{s.Oldest ?? "-"} → {s.Newest ?? "-"}");
        ConsoleUi.Field("后台监听", "未接线（W5-c 接入 Eztools.Desktop；当前用 clip capture 手动捕获）");
        return Task.FromResult(0);
    }

    private static int ParseLimit(CliArgs cli, int fallback) =>
        cli.Get("limit") is { } raw && int.TryParse(raw, out var n) && n > 0 ? n : fallback;

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
        : $"{bytes} B";
}
