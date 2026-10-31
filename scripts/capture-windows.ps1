# Best-effort native settings capture on the Windows build runner.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PresenceCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);
}
'@
$appProcess = Start-Process -FilePath './dist/windows-x64/DeveloperPresence.App.exe' -PassThru
try {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        Start-Sleep -Seconds 1
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw 'The app exited before its settings could be captured.' }
        if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($appProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'This runner has no visible app window.' }
    [PresenceCapture]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 1
    $windowRect = New-Object PresenceCapture+Rect
    if (![PresenceCapture]::GetWindowRect($appProcess.MainWindowHandle, [ref]$windowRect)) {
        throw 'The runner could not read the app window size.'
    }
    $windowWidth = $windowRect.Right - $windowRect.Left
    $windowHeight = $windowRect.Bottom - $windowRect.Top
    if ($windowWidth -le 0 -or $windowHeight -le 0) { throw 'The app window is not visible.' }
    $bitmap = New-Object System.Drawing.Bitmap($windowWidth, $windowHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($windowRect.Left, $windowRect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $PWD 'dist/windows-x64/settings.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
} finally {
    if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force }
}
