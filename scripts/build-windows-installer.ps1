# One-click, per-user installer and update feed, built with Velopack.
# Output: Deskchirp-<version>-windows-x64-setup.exe plus feed/ (releases.win.json and packages)
# that installed copies download updates from on GitHub Releases.
param(
    [string]$PayloadDir = 'dist/windows-x64',
    [string]$InstallerDir = 'dist/windows-installer',
    [string]$Version = '',
    # Fixtures skip delta generation, which downloads the previous release.
    [switch]$NoDelta
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vpkVersion = '1.2.161'  # Keep in step with the Velopack package in DeveloperPresence.App.csproj.
$repoUrl = 'https://github.com/geekynasir/deskchirp-discord'
if (!$Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$payload = (Resolve-Path $PayloadDir).Path
New-Item $InstallerDir -ItemType Directory -Force | Out-Null
$output = (Resolve-Path $InstallerDir).Path
foreach ($file in @('Deskchirp.exe', 'Deskchirp.dll', 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'Velopack.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
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
@{ name = 'Deskchirp for Discord'; version = $Version; commit = $sourceSha; platform = 'windows-x64'; build = $runUrl;
   source = "https://github.com/geekynasir/deskchirp-discord/tree/$sourceSha" } |
    ConvertTo-Json | Set-Content (Join-Path $payload 'BUILD-INFO.json') -Encoding utf8NoBOM

$tools = Join-Path $root 'dist/tools'
$vpk = Join-Path $tools 'vpk.exe'
if (!(Test-Path $vpk)) {
    & dotnet tool install vpk --version $vpkVersion --tool-path $tools
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the Velopack CLI.' }
}

$feed = Join-Path $output 'feed'
Remove-Item $feed -Recurse -Force -ErrorAction SilentlyContinue
New-Item $feed -ItemType Directory | Out-Null
if (!$NoDelta) {
    # Small delta updates from the newest published version. The first Velopack release has none.
    & $vpk download github --repoUrl $repoUrl --channel win --pre --outputDir $feed
    if ($LASTEXITCODE -ne 0) { Write-Host 'No previous Velopack release; publishing a full package only.' }
}

& $vpk pack --packId Deskchirp --packVersion $Version --packDir $payload --mainExe Deskchirp.exe `
    --packTitle 'Deskchirp for Discord' --packAuthors geekynasir --channel win --runtime win-x64 --outputDir $feed `
    --icon (Join-Path $root 'assets/brand/deskchirp.ico') `
    --splashImage (Join-Path $root 'assets/brand/setup-splash.png') --splashProgressColor '#80F1CF' `
    --shortcuts StartMenuRoot --exclude '.*\.pdb|settings.*\.png'
if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed: $LASTEXITCODE" }

$setup = Join-Path $output "Deskchirp-$Version-windows-x64-setup.exe"
Copy-Item (Join-Path $feed 'Deskchirp-win-Setup.exe') $setup -Force
foreach ($file in @('releases.win.json', "Deskchirp-$Version-full.nupkg")) {
    if (!(Test-Path (Join-Path $feed $file))) { throw "The update feed is missing $file." }
}
Copy-Item (Join-Path $payload 'BUILD-INFO.json') (Join-Path $output 'BUILD-INFO.json')
Write-Host "Built $setup"
