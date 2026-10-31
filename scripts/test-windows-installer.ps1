# Exercise the real installer, installed executable, in-place upgrade and uninstaller.
$ErrorActionPreference = 'Stop'
$version = (Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'VERSION') -Raw).Trim()
$setup = (Resolve-Path "dist/windows-installer/Deskchirp-$version-windows-x64-setup.exe").Path
$install = Join-Path $env:LOCALAPPDATA 'Programs/Deskchirp'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Deskchirp.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F5C10229-3D01-49BF-9655-BF2D2564B917}_is1'

function Invoke-Setup([string]$installer = $setup) {
    $process = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LOG=dist/installer-smoke.log' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
}
# A previous-version setup fixture proves that the stable app ID upgrades in place.
$fixtureVersion = '0.0.0-upgrade-check'
New-Item 'dist/installer-upgrade-fixture' -ItemType Directory -Force | Out-Null
$fixtureOutput = (Resolve-Path 'dist/installer-upgrade-fixture').Path
$payload = (Resolve-Path 'dist/windows-x64').Path
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
& $compiler '/DAppNumericVersion=0.0.0.0' "/DAppVersion=$fixtureVersion" "/DPayloadDir=$payload" "/DInstallerDir=$fixtureOutput" (Join-Path (Split-Path $PSScriptRoot -Parent) 'windows/installer/Deskchirp.iss')
if ($LASTEXITCODE -ne 0) { throw 'Could not build the previous-version installer fixture.' }
Invoke-Setup (Join-Path $fixtureOutput "Deskchirp-$fixtureVersion-windows-x64-setup.exe")
foreach ($file in @('Deskchirp.exe', 'Deskchirp.dll', 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'vcruntime140.dll', 'msvcp140.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'BUILD-INFO.json', 'Discord-artwork/deskchirp.ico', 'Discord-artwork/deskchirp.png', 'Discord-artwork/t3_code.png', 'Discord-artwork/harness_codex.png', 'unins000.exe')) {
    if (!(Test-Path (Join-Path $install $file))) { throw "Installed file missing: $file" }
}
if (!(Test-Path $shortcut)) { throw 'Start menu shortcut missing.' }
$entry = Get-ItemProperty $uninstallKey
if ($entry.DisplayVersion -ne $fixtureVersion) { throw 'Installed Apps version is incorrect.' }
if ($entry.InstallLocation.TrimEnd('\') -ne $install.Replace('/', '\').TrimEnd('\')) { throw 'Installed Apps points at another folder.' }
$metadata = Get-Content (Join-Path $install 'BUILD-INFO.json') -Raw | ConvertFrom-Json
if ($metadata.commit -ne $env:GITHUB_SHA -or $metadata.version -ne $version) { throw 'Installed source metadata is incorrect.' }

# Launch through the shortcut, not the build folder. Use a screenshot from the installed app.
$app = $null
try {
    Start-Process -FilePath $shortcut
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Seconds 1
        $app = Get-Process -Name Deskchirp -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $install 'Deskchirp.exe') } | Select-Object -First 1
        if ($app -and $app.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if (!$app -or $app.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The installed app did not open from Start.' }
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public class InstallerCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
    [InstallerCapture]::SetForegroundWindow($app.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 1
    $rect = New-Object InstallerCapture+Rect
    if (![InstallerCapture]::GetWindowRect($app.MainWindowHandle, [ref]$rect)) { throw 'Installed app window is not visible.' }
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $PWD 'dist/windows-installer/installed-settings.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
} finally { if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force } }

# Reinstall over the same app ID: upgrade keeps user data outside the program folder.
$settingsDir = Join-Path $env:LOCALAPPDATA 'DeveloperPresence'
New-Item $settingsDir -ItemType Directory -Force | Out-Null
$sentinel = Join-Path $settingsDir 'installer-preservation-check.txt'
Set-Content $sentinel 'keep user settings'
try {
    Invoke-Setup
    if (!(Test-Path $sentinel)) { throw 'Upgrade removed user settings.' }
    if ((Get-ItemProperty $uninstallKey).DisplayVersion -ne $version) { throw 'Upgrade did not update the Installed Apps version.' }
    $uninstaller = Join-Path $install 'unins000.exe'
    $remove = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    if ($remove.ExitCode -ne 0) { throw "Uninstall failed: $($remove.ExitCode)" }
    if ((Test-Path (Join-Path $install 'Deskchirp.exe')) -or (Test-Path $shortcut) -or (Test-Path $uninstallKey)) { throw 'Uninstall left the app, shortcut or registration behind.' }
    if (!(Test-Path $sentinel)) { throw 'Uninstall removed user settings.' }
} finally { Remove-Item $sentinel -ErrorAction SilentlyContinue }
Write-Host 'Installer verified: install, Start shortcut, launch, version upgrade, user-data preservation and uninstall.'
