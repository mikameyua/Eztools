# launch-lowil.ps1 -- Run a command line with a Low integrity token (P3 / M7 helper).
#
# Why this exists (2026-09-24, Win11 25H2):
#   - `runas /trustlevel:0x2000 ...` fails with error 1168 (element not found) even when the
#     Secondary Logon (seclogon) service is running -- mechanism-level breakage on 25H2.
#   - PsExec 2.43 `-low` does NOT drop the integrity level (verified: whoami /groups still High).
#   - This script manipulates the current process token directly:
#     OpenProcessToken -> DuplicateTokenEx -> SetTokenInformation(TokenIntegrityLevel, S-1-16-4096)
#     -> CreateProcessWithTokenW.
#
# Usage:
#   pwsh -File scripts\launch-lowil.ps1 -CommandLine 'cmd.exe /c <command> > out.txt 2>&1'
#
# IMPORTANT (no-write-up):
#   The child runs at Low IL and can only write to objects labeled Low. Redirect its output
#   into a folder prepared with:
#       icacls <dir> /setintegritylevel L
#
# Requires: elevated PowerShell (SeImpersonatePrivilege).

param([Parameter(Mandatory = $true)][string]$CommandLine)

$src = @'
using System;
using System.Runtime.InteropServices;

public static class EztoolsLowLaunch
{
    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, wCbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES { public IntPtr Sid; public int Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr h, uint acc, IntPtr attr, int imp, int type, out IntPtr newTok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSidToSidW(string s, out IntPtr ptr);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(IntPtr tok, int cls, ref TOKEN_MANDATORY_LABEL info, uint len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr tok, uint flags, string app, string cmd, uint create, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    public static string Run(string args)
    {
        IntPtr htok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x02000000, out htok))
            return "FAIL OpenProcessToken err=" + Marshal.GetLastWin32Error();
        IntPtr newTok;
        if (!DuplicateTokenEx(htok, 0x02000000, IntPtr.Zero, 2, 1, out newTok))
            return "FAIL DuplicateTokenEx err=" + Marshal.GetLastWin32Error();
        IntPtr sidPtr;
        if (!ConvertStringSidToSidW("S-1-16-4096", out sidPtr))
            return "FAIL ConvertStringSidToSid err=" + Marshal.GetLastWin32Error();
        var label = new TOKEN_MANDATORY_LABEL();
        label.Label.Sid = sidPtr;
        label.Label.Attributes = 0x20; /* SE_GROUP_INTEGRITY */
        uint len = (uint)(Marshal.SizeOf(typeof(TOKEN_MANDATORY_LABEL)) + 12);
        if (!SetTokenInformation(newTok, 25 /* TokenIntegrityLevel */, ref label, len))
            return "FAIL SetTokenInformation err=" + Marshal.GetLastWin32Error();
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        si.lpDesktop = Marshal.StringToHGlobalUni("WinSta0\\Default");
        PROCESS_INFORMATION pi;
        if (!CreateProcessWithTokenW(newTok, 0, null, args, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, null, ref si, out pi))
            return "FAIL CreateProcessWithTokenW err=" + Marshal.GetLastWin32Error();
        return "OK pid=" + pi.dwProcessId;
    }
}
'@

if (-not ('EztoolsLowLaunch' -as [type])) {
    Add-Type -TypeDefinition $src
}
[EztoolsLowLaunch]::Run($CommandLine)
