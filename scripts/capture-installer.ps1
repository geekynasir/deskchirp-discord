# Capture the one-click install window while setup runs, for review.
$ErrorActionPreference = 'Stop'
$version = (Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'VERSION') -Raw).Trim()
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class SetupCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
}
'@
$setup = Start-Process -FilePath "dist/windows-installer/Deskchirp-$version-windows-x64-setup.exe" -PassThru
$captured = $false
try {
    # The splash is shown only while files are extracted, so poll quickly.
    for ($attempt = 0; $attempt -lt 300 -and !$setup.HasExited -and !$captured; $attempt++) {
        Start-Sleep -Milliseconds 100
        $setup.Refresh()
        $window = $setup.MainWindowHandle
        if ($window -eq [IntPtr]::Zero -or ![SetupCapture]::IsWindowVisible($window)) { continue }
        Start-Sleep -Milliseconds 400
        $rect = New-Object SetupCapture+Rect
        if (![SetupCapture]::GetWindowRect($window, [ref]$rect) -or $rect.Right -le $rect.Left) { continue }
        $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $bitmap.Save((Join-Path $PWD 'dist/windows-installer/setup-progress.png'), [System.Drawing.Imaging.ImageFormat]::Png)
            $captured = $true
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    $setup.WaitForExit(120000) | Out-Null
} finally {
    Get-Process -Name Deskchirp -ErrorAction SilentlyContinue | Stop-Process -Force
}
if (!$captured) { throw 'The install window was not captured.' }
