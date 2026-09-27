# Wait until the foreground Explorer window's selection contains the target file.
# Used by verify-desktop.py (tray shellSelection acceptance branch).
# Poll condition is "selection contains target", NOT "foreground is CabinetWClass":
# other Explorer windows already in the foreground (Home / This PC) would satisfy
# that prematurely (hit in practice, see verify-desktop.py 2c notes).
#
# Foreground activation: `explorer /select` launched from a background process
# OPENS the window but does NOT steal focus (Windows foreground lock, seen
# 2026-09-24: dump showed the target window open + selected while [wait] kept
# TIMEOUT). We therefore actively activate the target window each poll round
# (AppActivate + ALT unlock trick) -- equivalent to a user clicking the window,
# which matches the real tray scenario "user acts on the foreground selection".
#
# MUST stay pure ASCII: executed by Windows PowerShell 5.1, which reads BOM-less
# UTF-8 scripts as ANSI(GBK) and silently garbles CJK literals (2026-09-24).
# Output contract: first stdout line starts with "OK" = selection seen;
# "TIMEOUT" = 20s elapsed; "COM_FAIL" = Shell.Application unavailable
# (previously indistinguishable from TIMEOUT -- skip evidence could not tell
# "desktop busy" from "COM broken", which blocked root-cause analysis).
param([string]$Target)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class U32W {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

Start-Process explorer.exe "/select,`"$Target`""

try {
    $shell = New-Object -ComObject Shell.Application
    if ($null -eq $shell) { throw "New-Object returned null" }
} catch {
    Write-Output ("COM_FAIL: Shell.Application unavailable: " + $_.Exception.Message)
    exit 2
}

$wscript = New-Object -ComObject WScript.Shell
$deadline = (Get-Date).AddSeconds(20)
$activated = $false

while ((Get-Date) -lt $deadline) {
    # Find the window showing the target selection (regardless of focus).
    $found = $null
    foreach ($w in $shell.Windows()) {
        try {
            $s = $w.Document.SelectedItems()
            for ($k = 0; $k -lt $s.Count; $k++) {
                if ($s.Item($k).Path -ieq $Target) { $found = $w; break }
            }
        } catch {}
        if ($null -ne $found) { break }
    }

    if ($null -ne $found) {
        # Not foreground yet -> activate it (user-click equivalent).
        if ([IntPtr]$found.HWND -ne [U32W]::GetForegroundWindow()) {
            $null = $wscript.AppActivate($found.HWND)
            Start-Sleep -Milliseconds 200
            if ([IntPtr]$found.HWND -ne [U32W]::GetForegroundWindow()) {
                # ALT keypress unlocks the OS foreground lock, then retry.
                $wscript.SendKeys('%')
                $null = [U32W]::SetForegroundWindow([IntPtr]$found.HWND)
                Start-Sleep -Milliseconds 200
            }
            continue
        }
        Write-Output "OK hwnd=$($found.HWND)"
        exit 0
    }
    Start-Sleep -Milliseconds 500
}

Write-Output "TIMEOUT"
exit 1
