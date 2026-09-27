# Dump all current Shell (Explorer) windows: index / hwnd / folder / selection.
# Diagnostic helper for the tray shellSelection acceptance branch (verify-desktop.py).
# MUST stay pure ASCII: it is executed by Windows PowerShell 5.1, which reads
# BOM-less UTF-8 scripts as ANSI(GBK) and silently garbles CJK literals
# (2026-09-24: "(no window)" came out as mojibake in the skip evidence).
# Also: surface COM failure explicitly instead of dying silently on stderr --
# "no windows" and "COM unavailable" looked identical before (un-Diagnosable skip).
try {
    $shell = New-Object -ComObject Shell.Application
    if ($null -eq $shell) { throw "New-Object returned null" }
} catch {
    Write-Output ("COM_FAIL: Shell.Application unavailable: " + $_.Exception.Message)
    exit 2
}

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
if ($i -eq 0) { Write-Output "(no shell window open)" }
