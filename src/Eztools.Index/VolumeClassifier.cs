// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Eztools.Index;

/// <summary>
/// 卷被跳过的**原因枚举**（W3-e-2）。为什么必须是枚举而不是布尔：
/// "跳过"的下一步动作就是"让用户知道哪些卷没被索引"—— 而"没索引"和"因为 exFAT 所以没索引"
/// 对用户是两件不同的事（前者要查，后者要去装/换盘）。布尔会让 UI 只能说"有卷被跳过"。
/// </summary>
public enum SkipReason
{
    /// <summary>非本地固定卷（可移动/网络/光驱）。</summary>
    NotFixed,

    /// <summary>卷未就绪（无介质 / 未挂载）。</summary>
    NotReady,

    /// <summary>无盘符卷（仅挂载点）—— 卷序列号与路径前缀都无可靠来源。</summary>
    NoDriveLetter,

    /// <summary>文件系统不支持（只有 NTFS 有 MFT + USN journal）。</summary>
    UnsupportedFileSystem,
}

/// <summary>被跳过的卷 + **具体原因**（<see cref="ReasonText"/> 供 UI 直接显示，非空）。</summary>
public sealed record SkippedVolume(string Volume, SkipReason Reason, string ReasonText);

/// <summary>
/// 卷**尝试后失败**的原因枚举（2026-09-25，缺口②）。
///
/// <b>为什么必须与 <see cref="SkipReason"/> 分开，而不是合并成一个"没索引"列表</b>：
/// <c>Skipped</c> = **从没试过**（前置条件不满足，我们主动放弃）；<c>Failed</c> = **试了没成**
/// （环境/权限/介质问题）。前者是配置问题、后者是环境问题 —— 合并之后"该去查配置还是去修环境"
/// 就永远分不开了。两者的**共同点**才是要合并的：都必须**结构化且可见**。
///
/// 反面教材（本枚举的立项理由）：原来失败只进 <c>service.LastError</c> 的自由文本，
/// 于是 <c>volumes=0 + skippedVolumes=0</c> 而**检测到 2 个卷** —— <c>0+0≠2</c> 却没有任何断言会红，
/// 因为守恒式被一段字符串绕过去了。**能机读，才守恒得住。**
/// </summary>
public enum FailureKind
{
    /// <summary>Win32 5 —— 访问被拒绝（无提权 / ACL 受限）。</summary>
    AccessDenied,

    /// <summary>Win32 21 —— 卷未就绪。</summary>
    NotReady,

    /// <summary>Win32 1167 —— 设备未连接。</summary>
    DeviceNotConnected,

    /// <summary>Win32 1005 —— 无法识别的文件系统（卷受损或未格式化）。</summary>
    UnrecognizedVolume,

    /// <summary>Win32 12 —— 驱动器号无效。</summary>
    InvalidDrive,

    /// <summary>Win32 1179 —— 卷未启用 USN 变更日志。</summary>
    JournalNotActive,

    /// <summary>
    /// <c>-32017</c>（<see cref="Eztools.Contracts.PrimitiveErrorCodes.CoreUnavailable"/>）——
    /// Core 特权服务不可达。**这是本机最常见的一种**（没提权跑 <c>ezt core start</c> 时
    /// 全卷都会落到这里），必须单独成一档，否则用户看到的是"其它原因"，而真正要做的事
    /// 只有一件：先起 Core。
    /// </summary>
    CoreUnavailable,

    /// <summary>其它（<see cref="FailedVolume.Code"/> **原码透传**，不吞）。</summary>
    Other,
}

/// <summary>
/// 尝试索引但失败的卷 + **结构化原因**。
/// <paramref name="Code"/> 一律原样透传（Core 侧的结构化错误码、或卷序列号读取失败的 <c>-1</c> 哨兵，
/// 见 <see cref="VolumeClassifier.SerialUnavailableCode"/>）—— **绝不归一成"失败"**，
/// 否则用户拿到的是"有个卷没成"而不是"D: 访问被拒绝"。
/// </summary>
public sealed record FailedVolume(string Volume, FailureKind Kind, int Code, string Message)
{
    /// <summary>原因的**短文案**（不含细节）。与 <see cref="SkippedVolume.ReasonText"/> 同口径 ——
    /// 状态行这类窄版面只放得下这一句，细节留在 <see cref="Message"/> 里。</summary>
    public string ReasonText => VolumeClassifier.FailureText(Kind, Code);
}

/// <summary>卷分类结果：进入索引的 + 被跳过的（带原因）。两者的并集 = 检测到的卷总数。</summary>
public sealed record VolumeScan(IReadOnlyList<string> Indexed, IReadOnlyList<SkippedVolume> Skipped);

/// <summary>盘符探测的输入（与 <c>DriveInfo</c> 解耦，便于零设备测试）。</summary>
public readonly record struct DriveDescriptor(string Root, string DriveType, bool IsReady, string? FileSystem);

/// <summary>
/// 卷筛选（W3-e-2）。<b>核心纪律：跳过必须可见且带原因 —— 绝不静默丢弃。</b>
///
/// 原实现（<c>DefaultVolumes</c>）用 <c>Where(DriveType.Fixed &amp;&amp; IsReady)</c> 直接过滤，
/// 被过滤掉的卷**没有任何记录**：用户看到"3 个卷已索引"，无从知道第 4 个卷为什么不在里面 ——
/// 这正是本项目 S9′/S11 家族（"真实依赖被藏起来"）的同款形态。本类把"跳过"变成一等输出。
///
/// 判定顺序（先到先得，保证原因是**最根本**的那条）：非固定 ⇒ 未就绪 ⇒ 无盘符 ⇒ 非 NTFS。
/// </summary>
public static class VolumeClassifier
{
    /// <summary>唯一支持的卷类型（MFT/USN 只存在于 NTFS —— 这是硬约束，不是可配置项）。</summary>
    public const string RequiredFileSystem = "NTFS";

    /// <summary>
    /// 分类给定盘符清单。返回的 <see cref="VolumeScan.Indexed"/> 已按
    /// <see cref="VolumeWorker.OrderVolumes"/> 归一排序（C: 优先）。
    /// </summary>
    public static VolumeScan Scan(IEnumerable<DriveDescriptor> drives)
    {
        ArgumentNullException.ThrowIfNull(drives);

        var candidates = new List<string>();
        var skipped = new List<SkippedVolume>();

        foreach (var d in drives)
        {
            var letter = ExtractLetter(d.Root);
            if (!string.Equals(d.DriveType, "Fixed", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(Make(d.Root, SkipReason.NotFixed));
                continue;
            }

            if (!d.IsReady)
            {
                skipped.Add(Make(d.Root, SkipReason.NotReady));
                continue;
            }

            if (letter is null)
            {
                skipped.Add(Make(d.Root, SkipReason.NoDriveLetter));
                continue;
            }

            if (!string.Equals(d.FileSystem, RequiredFileSystem, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(Make(d.Root, SkipReason.UnsupportedFileSystem, d.FileSystem));
                continue;
            }

            candidates.Add(letter.Value + ":");
        }

        return new VolumeScan(VolumeWorker.OrderVolumes(candidates), skipped);
    }

    /// <summary>真实盘符扫描（<c>DriveInfo.GetDrives()</c>）。<b>失败不抛</b>：单个盘符读不到信息按"未就绪"处理。</summary>
    public static VolumeScan ScanLocalDrives()
    {
        var descriptors = new List<DriveDescriptor>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            // DriveFormat 在未就绪时抛 IOException —— 正是"未就绪"这一分支的输入
            string? fs = null;
            var ready = false;
            try
            {
                ready = drive.IsReady;
                if (ready)
                {
                    fs = drive.DriveFormat;
                }
            }
            catch (IOException)
            {
                ready = false;
            }
            catch (UnauthorizedAccessException)
            {
                ready = false;
            }

            descriptors.Add(new DriveDescriptor(drive.Name, drive.DriveType.ToString(), ready, fs));
        }

        return Scan(descriptors);
    }

    /// <summary>原因 → 用户可读文案（**非空**，UI 直接显示；断言要求每项都落在这张表里）。</summary>
    public static string ReasonText(SkipReason reason, string? fileSystem = null) => reason switch
    {
        SkipReason.NotFixed => "非本地固定卷（可移动/网络/光驱）",
        SkipReason.NotReady => "卷未就绪（无介质或未挂载）",
        SkipReason.NoDriveLetter => "无盘符卷（仅挂载点）",
        SkipReason.UnsupportedFileSystem =>
            $"文件系统不支持（仅 {RequiredFileSystem} 有 MFT/USN：{(string.IsNullOrEmpty(fileSystem) ? "未知" : fileSystem)}）",
        _ => "未知原因",
    };

    private static SkippedVolume Make(string volume, SkipReason reason, string? fileSystem = null) =>
        new(volume, reason, ReasonText(reason, fileSystem));

    /// <summary>卷序列号读取失败的哨兵码（不是 Win32 码，避免与真实错误码撞号）。</summary>
    public const int SerialUnavailableCode = -1;

    /// <summary>
    /// 错误码 → <see cref="FailureKind"/>。**未收录的码归 <see cref="FailureKind.Other"/>，
    /// 但码本身原样保留**（"其它(1234)" 也比 "失败" 有用得多）。
    /// 码有两族：Win32 正数（卷/文件系统层）与 Core 侧的 RPC 负数（-320xx）。
    /// </summary>
    public static FailureKind Classify(int code) => code switch
    {
        5 => FailureKind.AccessDenied,
        21 => FailureKind.NotReady,
        1167 => FailureKind.DeviceNotConnected,
        1005 => FailureKind.UnrecognizedVolume,
        12 => FailureKind.InvalidDrive,
        1179 => FailureKind.JournalNotActive,
        Eztools.Contracts.PrimitiveErrorCodes.CoreUnavailable => FailureKind.CoreUnavailable,
        _ => FailureKind.Other,
    };

    /// <summary>
    /// 失败原因 → 用户可读文案（**非空**，UI 直接显示）。与 <see cref="ReasonText"/> 同风格：
    /// 说清"为什么不成"，而不是"有卷没成"。未收录码走"其它原因（错误码 N）"——**数字必须出现**。
    /// 注意措辞是"错误码"不是"Win32"：码有两族（Win32 正数 / RPC 负数），
    /// 把 -32017 叫 Win32 会把排查方向带偏。
    /// </summary>
    public static string FailureText(FailureKind kind, int code) => kind switch
    {
        FailureKind.AccessDenied => "访问被拒绝（需要管理员权限）",
        FailureKind.NotReady => "卷未就绪",
        FailureKind.DeviceNotConnected => "设备未连接",
        FailureKind.UnrecognizedVolume => "无法识别的文件系统（卷可能受损或未格式化）",
        FailureKind.InvalidDrive => "驱动器号无效",
        FailureKind.JournalNotActive => "卷未启用变更日志（journal）",
        FailureKind.CoreUnavailable => "Core 特权服务未运行（建索引需要它）",
        _ => $"其它原因（错误码 {code}）",
    };

    /// <summary>构造一条结构化失败项（文案自动带卷名之外的原因，调用方决定 Message 细节）。</summary>
    public static FailedVolume Failure(string volume, int code, string? detail = null)
    {
        var kind = code == SerialUnavailableCode ? FailureKind.NotReady : Classify(code);
        var text = FailureText(kind, code);
        return new FailedVolume(volume, kind, code,
            string.IsNullOrWhiteSpace(detail) ? text : $"{text}：{detail}");
    }

    /// <summary>盘符提取（与 <see cref="VolumeWorker.OrderVolumes"/> 同口径）。</summary>
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
