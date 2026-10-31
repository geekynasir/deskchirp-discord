# Capture the actual setup wizard before exercising silent installation.
$ErrorActionPreference = 'Stop'
$version = (Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'VERSION') -Raw).Trim()
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class SetupCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
$began = Get-Date
$launcher = Start-Process -FilePath "dist/windows-installer/Deskchirp-$version-windows-x64-setup.exe" -ArgumentList '/SP-' -PassThru
$wizard = $null
try {
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Seconds 1
        $wizard = Get-Process | Where-Object { $_.MainWindowTitle -like 'Setup*Deskchirp*' -and $_.StartTime -ge $began.AddSeconds(-2) } | Select-Object -First 1
        if ($wizard) { break }
    }
    if (!$wizard) { throw 'The setup wizard did not open.' }
    [SetupCapture]::SetForegroundWindow($wizard.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 1
    $rect = New-Object SetupCapture+Rect
    if (![SetupCapture]::GetWindowRect($wizard.MainWindowHandle, [ref]$rect)) { throw 'The setup window is not visible.' }
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $PWD 'dist/windows-installer/setup-welcome.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
} finally {
    if ($wizard -and !$wizard.HasExited) { Stop-Process -Id $wizard.Id -Force }
    if (!$launcher.HasExited) { Stop-Process -Id $launcher.Id -Force }
}
