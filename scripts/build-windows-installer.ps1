# Self-contained, per-user installer. Build tools and CRT come from the hosted VS toolchain.
param(
    [string]$PayloadDir = 'dist/windows-x64',
    [string]$InstallerDir = 'dist/windows-installer'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$payload = (Resolve-Path $PayloadDir).Path
New-Item $InstallerDir -ItemType Directory -Force | Out-Null
$output = (Resolve-Path $InstallerDir).Path
foreach ($file in @('Deskchirp.exe', 'Deskchirp.dll', 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    if (!(Test-Path (Join-Path $payload $file))) { throw "Missing application file: $file" }
}

# Windows 10 supplies the UCRT. Ship the VC++ CRT app-locally so a clean PC
# needs neither an extra runtime download nor administrator access.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'The Visual C++ build tools are required.' }
$crt = Get-ChildItem (Join-Path $visualStudio 'VC/Redist/MSVC') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64/Microsoft.VC143.CRT' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$crt) { throw 'The Visual C++ x64 redistributable files were not found.' }
Copy-Item (Join-Path $crt '*.dll') $payload -Force
foreach ($file in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
    if (!(Test-Path (Join-Path $payload $file))) { throw "Missing Visual C++ runtime: $file" }
}

Copy-Item (Join-Path $root 'licenses/visual-cpp-runtime.md') (Join-Path $payload 'licenses/visual-cpp-runtime.md') -Force

$sourceSha = (& git -C $root rev-parse HEAD).Trim()
$runUrl = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { '' }
@{ name = 'Deskchirp for Discord'; version = $version; commit = $sourceSha; platform = 'windows-x64'; build = $runUrl;
   source = "https://github.com/geekynasir/deskchirp-discord/tree/$sourceSha" } |
    ConvertTo-Json | Set-Content (Join-Path $payload 'BUILD-INFO.json') -Encoding utf8NoBOM

$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (!(Test-Path $compiler)) { throw 'Inno Setup 6 is required to build the Windows installer.' }
Copy-Item (Join-Path (Split-Path $compiler -Parent) 'License.txt') (Join-Path $payload 'licenses/innosetup-license.txt')
$numericVersion = ($version -split '-')[0] + '.0'
Write-Host "Inno Setup $((Get-Item $compiler).VersionInfo.ProductVersion)"
& $compiler "/DAppNumericVersion=$numericVersion" "/DAppVersion=$version" "/DPayloadDir=$payload" "/DInstallerDir=$output" (Join-Path $root 'windows/installer/Deskchirp.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$setup = Join-Path $output "Deskchirp-$version-windows-x64-setup.exe"
if (!(Test-Path $setup)) { throw 'The installer was not produced.' }
Copy-Item (Join-Path $payload 'BUILD-INFO.json') (Join-Path $output 'BUILD-INFO.json')
Write-Host "Built $setup"
