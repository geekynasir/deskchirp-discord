# Exercise the real one-click installer on a disposable CI machine: replacing a preview 3
# wizard install, Start launch, an in-place update through Velopack's updater, user-data
# preservation and uninstall.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$setup = (Resolve-Path "dist/windows-installer/Deskchirp-$version-windows-x64-setup.exe").Path
$install = Join-Path $env:LOCALAPPDATA 'Deskchirp'
$app = Join-Path $install 'current'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Deskchirp for Discord.lnk'
$legacyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F5C10229-3D01-49BF-9655-BF2D2564B917}_is1'
$legacyFolder = Join-Path $env:LOCALAPPDATA 'Programs/Deskchirp'

# Start-Process -Wait would also wait for the Deskchirp window setup opens, so wait for the program alone.
function Invoke-Program([string]$file, [string[]]$arguments, [string]$what) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru
    $null = $process.Handle  # Keeps the exit code readable after exit.
    if (!$process.WaitForExit(180000)) {
        if (Test-Path 'dist/installer-smoke.log') { Get-Content 'dist/installer-smoke.log' -Tail 40 | Out-Host }
        throw "$what did not finish within 3 minutes."
    }
    if ($process.ExitCode -ne 0) { throw "$what failed: $($process.ExitCode)" }
}
function Invoke-Setup([string]$installer) {
    Invoke-Program $installer @('--silent', '--log', (Join-Path $PWD 'dist/installer-smoke.log')) 'Setup'
}
function Stop-Deskchirp {
    Get-Process -Name Deskchirp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}
# Velopack registers under the package ID; find it by name so a different key layout still fails clearly.
function Get-UninstallEntry {
    Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.DisplayName -eq 'Deskchirp for Discord' -and $_.PSChildName -notlike '{F5C10229*' } | Select-Object -First 1
}
# Installed Apps shows only the numeric part (0.1.0); the installed build info has the full version.
function Get-InstalledVersion {
    $info = Join-Path $app 'BUILD-INFO.json'
    if (!(Test-Path $info)) { return $null }
    $full = (Get-Content $info -Raw | ConvertFrom-Json).version
    if ((Get-UninstallEntry).DisplayVersion -ne ($full -split '-')[0]) { return "$full (Installed Apps: $((Get-UninstallEntry).DisplayVersion))" }
    $full
}
function Wait-InstalledVersion([string]$expected, [string]$step) {
    for ($attempt = 0; $attempt -lt 30 -and (Get-InstalledVersion) -ne $expected; $attempt++) { Start-Sleep -Seconds 1 }
    if ((Get-InstalledVersion) -eq $expected) { return }
    Write-Host "Uninstall entries:"; Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object { Get-ItemProperty $_.PSPath } | Format-List PSChildName, DisplayName, DisplayVersion, InstallLocation, QuietUninstallString | Out-Host
    Get-ChildItem $install -ErrorAction SilentlyContinue | Out-Host
    if (Test-Path 'dist/installer-smoke.log') { Get-Content 'dist/installer-smoke.log' -Tail 60 | Out-Host }
    throw "$step expected version $expected, found '$(Get-InstalledVersion)'."
}

# 1. Someone on preview 3 installed the old wizard. Installing must replace it, not add a second copy.
$legacySetup = Join-Path $PWD 'dist/legacy-preview-3-setup.exe'
Invoke-WebRequest 'https://github.com/geekynasir/deskchirp-discord/releases/download/v0.1.0-preview.3/Deskchirp-0.1.0-preview.3-windows-x64-setup.exe' -OutFile $legacySetup
Invoke-Program $legacySetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-') 'Preview 3 setup'
if (!(Test-Path $legacyKey)) { throw 'Could not install the preview 3 fixture.' }

# 2. Install an older one-click version first so the real build arrives as an update.
$fixtureVersion = '0.0.1-update-check'
& (Join-Path $PSScriptRoot 'build-windows-installer.ps1') -InstallerDir 'dist/installer-update-fixture' -Version $fixtureVersion -NoDelta
Invoke-Setup (Resolve-Path "dist/installer-update-fixture/Deskchirp-$fixtureVersion-windows-x64-setup.exe").Path
Stop-Deskchirp
if ((Test-Path $legacyKey) -or (Test-Path (Join-Path $legacyFolder 'Deskchirp.exe'))) { throw 'The preview 3 install was not removed.' }
Wait-InstalledVersion $fixtureVersion 'Setup'
# The fixture rewrote the payload's BUILD-INFO.json; restore the real build's copy.
Copy-Item 'dist/windows-installer/BUILD-INFO.json' 'dist/windows-x64/BUILD-INFO.json' -Force

# 3. Settings live outside the install folder and must survive updates and uninstall.
$settingsDir = Join-Path $env:LOCALAPPDATA 'DeveloperPresence'
New-Item $settingsDir -ItemType Directory -Force | Out-Null
$sentinel = Join-Path $settingsDir 'installer-preservation-check.txt'
Set-Content $sentinel 'keep user settings'

try {
    # 4. Apply the real build the way the in-app updater does.
    $package = (Resolve-Path "dist/windows-installer/feed/Deskchirp-$version-full.nupkg").Path
    Invoke-Program (Join-Path $install 'Update.exe') @('--silent', 'apply', '--package', "`"$package`"") 'Update'
    Stop-Deskchirp
    Wait-InstalledVersion $version 'Update'
    if (!(Test-Path $sentinel)) { throw 'Update removed user settings.' }

    foreach ($file in @('Deskchirp.exe', 'Deskchirp.dll', 'Velopack.dll', 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'vcruntime140.dll', 'msvcp140.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'BUILD-INFO.json', 'Discord-artwork/deskchirp.ico', 'Discord-artwork/deskchirp.png', 'Discord-artwork/t3_code.png', 'Discord-artwork/status_working.png')) {
        if (!(Test-Path (Join-Path $app $file))) { throw "Installed file missing: $file" }
    }
    if (!(Test-Path $shortcut)) { throw 'Start menu shortcut missing.' }
    $metadata = Get-Content (Join-Path $app 'BUILD-INFO.json') -Raw | ConvertFrom-Json
    if ($metadata.commit -ne $env:GITHUB_SHA -or $metadata.version -ne $version) { throw 'Installed source metadata is incorrect.' }

    # 5. Launch through Start, not the build folder, and capture the installed app.
    $running = $null
    try {
        Start-Process -FilePath $shortcut
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Seconds 1
            $running = Get-Process -Name Deskchirp -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$install*" -and $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
            if ($running) { break }
        }
        if (!$running) { throw 'The installed app did not open from Start.' }
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
        [InstallerCapture]::SetForegroundWindow($running.MainWindowHandle) | Out-Null
        Start-Sleep -Seconds 1
        $rect = New-Object InstallerCapture+Rect
        if (![InstallerCapture]::GetWindowRect($running.MainWindowHandle, [ref]$rect)) { throw 'Installed app window is not visible.' }
        $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $bitmap.Save((Join-Path $PWD 'dist/windows-installer/installed-settings.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    } finally { Stop-Deskchirp }

    # 6. Running setup again over an installed copy repairs it in place.
    Invoke-Setup $setup
    Stop-Deskchirp
    Wait-InstalledVersion $version 'Reinstall'
    if (!(Test-Path (Join-Path $app 'Deskchirp.exe'))) { throw 'Reinstalling over the same version failed.' }

    # 7. Uninstall with the command Windows Installed Apps uses.
    $quiet = (Get-UninstallEntry).QuietUninstallString
    if ($quiet -notmatch '^"([^"]+)"\s*(.*)$') { throw "Unexpected uninstall command: $quiet" }
    Invoke-Program $Matches[1] @($Matches[2]) 'Uninstall'
    for ($attempt = 0; $attempt -lt 15 -and (Test-Path (Join-Path $app 'Deskchirp.exe')); $attempt++) { Start-Sleep -Seconds 1 }
    if ((Test-Path (Join-Path $app 'Deskchirp.exe')) -or (Test-Path $shortcut) -or (Get-UninstallEntry)) { throw 'Uninstall left the app, shortcut or registration behind.' }
    if (!(Test-Path $sentinel)) { throw 'Uninstall removed user settings.' }
} finally { Remove-Item $sentinel -ErrorAction SilentlyContinue }
Write-Host 'Installer verified: preview 3 replaced, update applied, Start launch, reinstall, user-data preservation and uninstall.'
