using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace Eztools.Core;

/// <summary>
/// 调用方身份与完整性级别校验（实施方案 §3 的第 2、3 道闸）。
///
/// 第 1 道闸（管道 ACL：仅当前用户 + SYSTEM）在 <see cref="CoreServer"/> 创建管道时已生效；
/// 这里在连接建立后再做身份确认（防御纵深），并拒绝低完整性调用方
/// （设计方案 §9.2 原则 4，防 UIPI 式攻击）。
///
/// 实现走 <c>GetNamedPipeClientProcessId → OpenProcessToken</c>：
/// 直接核对对端进程令牌的 SID 与完整性级别。<b>不使用模拟</b>（ImpersonateNamedPipeClient
/// 依赖 SeImpersonate 特权，普通用户进程上不可靠）；副作用是拿到的 callerPid 是
/// <b>经过验证的对端真实 PID</b>，比客户端自报可信，直接进审计与授权（terminate 守卫）。
///
/// 有意**不**要求"调用方完整性 ≥ Core 自身完整性"：提权后的 Core 是 High，
/// 若拒绝 Medium 的宿主/UI，"提权只为少数原语、其余正常用"的设计就废了。
/// 真正的边界是有限原语 + 参数校验 + 审计，不是完整性对齐（见 P3 方案 §3）。
/// </summary>
public static class CoreSecurity
{
    /// <summary>调用方必须达到的最低完整性级别：Medium（0x2000）。</summary>
    public const uint MinimumIntegrity = NativeInterop.IntegrityMedium;

    public sealed record CheckResult(bool Ok, string CallerUser, int CallerPid, string? RejectionReason);

    /// <summary>
    /// 校验刚连上的管道客户端。必须在读任何数据之前调用。
    /// 失败返回理由；调用方只收到 CallerRejected，不泄露细节差异。
    /// </summary>
    public static CheckResult ValidateCaller(NamedPipeServerStream pipe)
    {
        try
        {
            return Inspect(pipe);
        }
        catch (Exception ex)
        {
            return new CheckResult(false, "", -1, "身份校验异常：" + ex.Message);
        }
    }

    private static CheckResult Inspect(NamedPipeServerStream pipe)
    {
        // 1) 对端真实 PID（内核给出，客户端骗不了）
        if (!NativeInterop.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid)
            || clientPid == 0)
        {
            return new CheckResult(false, "", -1, "无法获取调用方 PID");
        }

        // 2) 以最小权限打开对端进程（QUERY_LIMITED：对提权/系统进程也能开），再取其令牌
        var processHandle = NativeInterop.OpenProcess(
            NativeInterop.ProcessQueryLimitedInformation, false, clientPid);
        if (processHandle == IntPtr.Zero)
        {
            return new CheckResult(false, "", (int)clientPid, "无法打开调用方进程");
        }

        try
        {
            if (!NativeInterop.OpenProcessToken(processHandle, NativeInterop.TokenQuery, out var token))
            {
                return new CheckResult(false, "", (int)clientPid, "无法打开调用方令牌");
            }

            try
            {
                // WindowsIdentity(IntPtr) 会复制句柄，原句柄由 CloseHandle 释放
                using var identity = new WindowsIdentity(token);

                var coreSid = WindowsIdentity.GetCurrent().User?.Value;
                var callerSid = identity.User?.Value;
                if (string.IsNullOrEmpty(callerSid)
                    || !string.Equals(callerSid, coreSid, StringComparison.OrdinalIgnoreCase))
                {
                    return new CheckResult(false, identity.Name, (int)clientPid, "调用方用户与 Core 用户不一致");
                }

                var level = NativeInterop.GetTokenIntegrityLevel(token);
                if (level < MinimumIntegrity)
                {
                    return new CheckResult(
                        false, identity.Name, (int)clientPid,
                        $"调用方完整性级别 0x{level:X} 低于最低要求 0x{MinimumIntegrity:X}");
                }

                return new CheckResult(true, identity.Name, (int)clientPid, null);
            }
            finally
            {
                NativeInterop.CloseHandle(token);
            }
        }
        finally
        {
            NativeInterop.CloseHandle(processHandle);
        }
    }
}
