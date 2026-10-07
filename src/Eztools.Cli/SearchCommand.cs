// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Nodes;
using Eztools.Host;
using Eztools.Host.Config;
using Eztools.Host.Search;

namespace Eztools.Cli;

/// <summary>
/// <c>ezt search</c> 子命令（2026-09-25 缺口① 落地）。
///
/// <list type="bullet">
/// <item><c>ezt search status</c> —— 起一个**临时**索引实例，把索引到底长什么样问清楚并打印
/// （就绪 / 总条目 / 卷清单 / 跳过卷 / 失败卷 / 暂停态 + 三档守恒自检）。</item>
/// <item><c>ezt search pause | resume</c> —— **显式拒绝**并说明去处（见下方"为什么不做"）。</item>
/// </list>
///
/// <b>为什么 pause/resume 不做成 CLI</b>（这个"不做"是有据的，不是漏做）：
/// 索引进程是**宿主的 stdio 子进程**（<see cref="SearchIndexProcess"/> 由托盘拉起并持有），
/// 除 stdio 之外**没有任何跨进程端点** —— 没有给索引进程准备的 <c>core.json</c> 那种发现文件。
/// 所以 CLI 就算拿到了"暂停"的意图，能操作的也只有它自己临时起的那个实例，退出即消失，
/// 对用户真正在用的索引**毫无影响**。做一个"看起来能用、实际改了别的实例"的命令，
/// 正是本项目一贯拒绝的**假成功**。
///
/// 真正的暂停入口是<b>托盘菜单「搜索索引 → 暂停/恢复索引更新」</b>与<b>搜索窗状态行徽标</b>
/// （两者都做了"暂停必须可见"的显示）。要 CLI 形态就得先给索引进程加端点文件 + 命名管道服务，
/// 那是架构改动（登记在 <c>docs/W3-缺口完善方案.md</c> §4 P1 之后的待办），不在本次范围。
/// </summary>
internal static class SearchCommand
{
    public static async Task<int> RunAsync(CliArgs cli)
    {
        var sub = cli.Rest.Count > 0 ? cli.Rest[0] : "status";
        ConsoleUi.JsonMode = cli.GetBool("json");

        return sub switch
        {
            "status" => await StatusAsync(cli).ConfigureAwait(false),
            "pause" or "resume" => RefuseControl(cli, sub),
            _ => UnknownSub(sub),
        };
    }

    private static int UnknownSub(string sub)
    {
        ConsoleUi.Error($"未知子命令: search {sub}（可用：status；pause/resume 会被显式拒绝，见其说明）");
        return 64;
    }

    private static int RefuseControl(CliArgs cli, string sub)
    {
        // 显式拒绝优于静默假装成功（§13 反模式清单同款）。
        var message =
            $"`ezt search {sub}` 不支持 —— 索引进程是宿主的 stdio 子进程，没有跨进程端点，"
            + "CLI 只能操作自己临时起的实例（改了等于没改）。\n"
            + "  真正的入口：托盘菜单「搜索索引 → " + (sub == "pause" ? "暂停" : "恢复") + "索引更新」，"
            + "或搜索窗底部状态行的「索引已暂停」徽标（点击即恢复）。";

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["ok"] = false,
                ["command"] = $"search.{sub}",
                ["reason"] = "no-cross-process-endpoint",
                ["message"] = message,
                ["alternative"] = "tray: 搜索索引 → " + (sub == "pause" ? "暂停" : "恢复") + "索引更新",
            });
        }
        else
        {
            ConsoleUi.Error(message);
        }

        return 64;
    }

    // ── status ──

    private static async Task<int> StatusAsync(CliArgs cli)
    {
        var paths = EztoolsPaths.Create(cli.Get("install-root"), cli.Get("config-root"));
        var waitReady = int.TryParse(cli.Get("wait-ready"), out var w) ? w : 3_000;

        // W11-a：CLI 的临时索引实例与宿主托盘一样带上 index.exclude 配置 ——
        // 否则 `ezt search status` 报出的 excludedFrns 与用户真实索引是两个世界（观察面失真）。
        // W11-b：search.pathFilter 初始限定同款透传。
        string? excludeRules = null;
        string? pathFilter = null;
        try
        {
            using var log = new HostLog(paths, echoToConsole: false);
            var configs = new ConfigStore(paths, log);
            excludeRules = HostSettingsSchema.TryGetString(configs, HostSettingsSchema.KeyIndexExclude);
            pathFilter = HostSettingsSchema.TryGetString(configs, HostSettingsSchema.KeySearchPathFilter);
        }
        catch (Exception)
        {
            // review-guards:allow-empty-catch :: 读不到配置 = 按零排除/无限定跑（status 仍如实回报）
        }

        using var index = new SearchIndexProcess(paths, paths.Root, excludeRules, pathFilter);

        if (SearchIndexProcess.FindIndexExe(paths) is null)
        {
            ConsoleUi.Error("找不到 ezt-index 可执行文件（安装不完整？试 `ezt install`）");
            return 4;
        }

        var client = new SearchIndexClient(index);

        var pong = await index.PingAsync().ConfigureAwait(false);
        if (pong is null)
        {
            ConsoleUi.Error("索引进程没有应答 ping（拉起失败或协议不通）");
            return 5;
        }

        // 索引是**后台自举**：ping 通了不代表能查。等到 ready 或超时，如实报当时状态。
        SearchStatusDto? status = null;
        var waited = 0;
        var first = true;
        while (first || (waited < waitReady && status?.Ready != true))
        {
            first = false;
            try
            {
                status = await client.StatusAsync().ConfigureAwait(false);
                if (status.Ready)
                {
                    break;
                }
            }
            catch (SearchIndexException)
            {
                // review-guards:allow-empty-catch :: 自举期间端点可能还没就绪 —— 继续等，不当作失败
            }

            await Task.Delay(100).ConfigureAwait(false);
            waited += 100;
        }

        if (status is null)
        {
            ConsoleUi.Error($"索引状态取不到（等待 {waited} ms）");
            return 5;
        }

        // ★ 三档守恒自检：indexed + skipped + failed == detected。
        //   在一段自由文本就能把失败藏起来之后（缺口②），这条式子必须**由命令自己算一遍**，
        //   否则命令报出来的数字又是自说自话。
        var indexed = status.Volumes.Count;
        var accounted = indexed + status.Skipped.Count + status.Failed.Count;
        var conserved = status.DetectedVolumes == 0 || accounted == status.DetectedVolumes;

        if (cli.GetBool("json"))
        {
            ConsoleUi.PrintJson(new JsonObject
            {
                ["ready"] = status.Ready,
                ["totalFiles"] = status.TotalFiles,
                ["paused"] = status.Paused,
                ["volumes"] = new JsonArray(status.Volumes.Select(v => (JsonNode)v).ToArray()),
                ["skippedVolumes"] = new JsonArray(status.Skipped.Select(s => (JsonNode)new JsonObject
                {
                    ["volume"] = s.Volume,
                    ["reason"] = s.Reason,
                    ["reasonText"] = s.ReasonText,
                }).ToArray()),
                ["failedVolumes"] = new JsonArray(status.Failed.Select(f => (JsonNode)new JsonObject
                {
                    ["volume"] = f.Volume,
                    ["kind"] = f.Kind,
                    ["code"] = f.Code,
                    ["reasonText"] = f.ReasonText,
                    ["message"] = f.Message,
                }).ToArray()),
                ["detectedVolumes"] = status.DetectedVolumes,
                ["accounted"] = accounted,
                ["conserved"] = conserved,
                // W11-a：用户排除可见性（协议 search.status 两字段的 CLI 面 —— 两层都必须能看见）
                ["excludeRules"] = status.ExcludeRules,
                ["excludedFrns"] = status.ExcludedFrns,
                // W11-b：pathFilter 限定可见性（同上）
                ["pathFilter"] = status.PathFilter,
                ["pathFilterAnchored"] = status.PathFilterAnchored,
                ["pathFilterReason"] = status.PathFilterReason,
                ["waitedMs"] = waited,
            }, cli.GetBool("compact"));
        }
        else
        {
            ConsoleUi.Header("搜索索引状态");
            ConsoleUi.Field("就绪", status.Ready ? "是" : "否（仍在自举）");
            ConsoleUi.Field("总条目", status.TotalFiles.ToString("N0"));
            ConsoleUi.Field("暂停", status.Paused ? "是（搜索结果可能过时）" : "否");
            ConsoleUi.Field("排除规则", status.ExcludeRules > 0
                ? $"{status.ExcludeRules} 条（作用域 {status.ExcludedFrns:N0} 条）"
                : "未配置");
            ConsoleUi.Field("限定目录", status.PathFilter.Length == 0
                ? "未配置"
                : status.PathFilterAnchored
                    ? $"{status.PathFilter}（已锚定）"
                    : $"{status.PathFilter}（⚠ 未生效：{status.PathFilterReason}）");
            ConsoleUi.Field("已索引卷", status.Volumes.Count == 0
                ? "（无）"
                : string.Join("、", status.Volumes));
            ConsoleUi.Field("检测到的卷", status.DetectedVolumes.ToString());

            foreach (var s in status.Skipped)
            {
                ConsoleUi.Info($"跳过 {s.Volume} —— {s.ReasonText}（{s.Reason}）");
            }

            foreach (var f in status.Failed)
            {
                ConsoleUi.Warn($"失败 {f.Volume} —— {f.ReasonText}（{f.Kind} / code={f.Code}）：{f.Message}");
            }

            if (conserved)
            {
                ConsoleUi.Ok($"守恒自检通过：{indexed} 已索引 + {status.Skipped.Count} 跳过 "
                    + $"+ {status.Failed.Count} 失败 = {accounted}（检测到 {status.DetectedVolumes}）");
            }
            else
            {
                ConsoleUi.Fail(
                    $"守恒自检失败：{indexed} + {status.Skipped.Count} + {status.Failed.Count} "
                    + $"= {accounted} ≠ 检测到 {status.DetectedVolumes}",
                    "有卷既没进索引、也没被跳过或失败的记录（S9′ 同族：真实依赖被藏起来了）");
            }
        }

        return status.Ready && conserved ? 0 : 1;
    }
}
