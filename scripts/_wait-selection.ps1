# 等待"前台资源管理器窗口的选中项包含目标文件"（速览 shellSelection 验收的辅助脚本）。
# 为什么轮询条件是"选中项包含目标"而不是"前台是 CabinetWClass"：
#   CabinetWClass 判据会被已在前台的其它 Explorer 窗口（Home/此电脑）提前满足 —— 实测踩到。
param([string]$Target)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class U32W {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

Start-Process explorer.exe "/select,`"$Target`""
$shell = New-Object -ComObject Shell.Application
$deadline = (Get-Date).AddSeconds(20)

while ((Get-Date) -lt $deadline) {
    $fg = [U32W]::GetForegroundWindow()
    foreach ($w in $shell.Windows()) {
        try {
            if ([IntPtr]$w.HWND -ne $fg) { continue }
            $s = $w.Document.SelectedItems()
            for ($k = 0; $k -lt $s.Count; $k++) {
                if ($s.Item($k).Path -ieq $Target) {
                    Write-Output "OK hwnd=$($w.HWND)"
                    exit 0
                }
            }
        } catch {}
    }
    Start-Sleep -Milliseconds 500
}

Write-Output "TIMEOUT"
exit 1
