# 枚举当前所有 Shell 窗口条目（诊断辅助）：HWND / 文件夹 / 选中项
$shell = New-Object -ComObject Shell.Application
$i = 0
foreach ($w in $shell.Windows()) {
    $folder = ""
    $sel = ""
    try { $folder = $w.Document.Folder.Self.Path } catch { $folder = "?" }
    try {
        $s = $w.Document.SelectedItems()
        $parts = @()
        for ($k = 0; $k -lt $s.Count; $k++) { $parts += $s.Item($k).Path }
        $sel = $parts -join " | "
    } catch { $sel = "?" }
    Write-Output "[$i] hwnd=0x$($w.HWND.ToString('X')) folder=$folder selected=$sel"
    $i++
}
if ($i -eq 0) { Write-Output "(没有打开的资源管理器窗口)" }
