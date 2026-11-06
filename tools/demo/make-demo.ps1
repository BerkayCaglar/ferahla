# Builds an invented demo disk for screenshots: a folder tree with made-up projects, caches and media, scanned twice
# into a separate Ferahla home, with the drive size replaced by a neutral 512 GB. Nothing from the real machine shows:
# start the UI with FERAHLA_DEMO=1 (drives come from the scans, the apps list stays empty).
#
#   powershell -NoProfile -File tools/demo/make-demo.ps1 -Exe publish\ferahla.exe
#   $env:FERAHLA_HOME = "$env:TEMP\ferahla-demo-home"; $env:FERAHLA_DEMO = '1'; publish\ferahla.exe
#
# Remove afterwards: the demo folder (-Root) and the demo home. (ASCII only: Windows PowerShell 5.1.)
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string]$Root = 'C:\FerahlaDemo',
    [string]$DemoHome = (Join-Path $env:TEMP 'ferahla-demo-home')
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path

function F([string]$rel, [double]$mb, [int]$days, [byte]$seed = 0) {
    $p = Join-Path $Root $rel
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    $fs = [IO.File]::Create($p)
    if ($seed -eq 0) { $fs.SetLength([int64]($mb * 1MB)) }
    else {
        $buf = New-Object byte[] (1MB)
        for ($i = 0; $i -lt $buf.Length; $i++) { $buf[$i] = [byte](($seed * 7 + $i) % 251) }
        for ($i = 0; $i -lt [int]$mb; $i++) { $fs.Write($buf, 0, $buf.Length) }
    }
    $fs.Close()
    $t = (Get-Date).AddDays(-$days)
    (Get-Item $p).LastWriteTime = $t
    (Get-Item $p).CreationTime = $t
}
function Marker([string]$rel, [string]$text = '{}') { $p = Join-Path $Root $rel; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; Set-Content $p $text }

if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
if (Test-Path $DemoHome) { Remove-Item -Recurse -Force $DemoHome }

# Projects with build outputs
Marker 'Projects\recipe-app\package.json'
F 'Projects\recipe-app\node_modules\framework\dist.bin' 180 20
F 'Projects\recipe-app\node_modules\.cache\babel.bin' 64 5
F 'Projects\recipe-app\src\main.js' 1 3 3
Marker 'Projects\tile-engine\Cargo.toml' '[package]'
F 'Projects\tile-engine\target\debug\deps\engine.bin' 420 12
F 'Projects\tile-engine\target\release\engine.bin' 160 12
F 'Projects\tile-engine\src\lib.rs' 1 9 5
New-Item -ItemType Directory -Force (Join-Path $Root 'Projects\orbit-game\Assets'), (Join-Path $Root 'Projects\orbit-game\ProjectSettings') | Out-Null
Marker 'Projects\orbit-game\ProjectSettings\ProjectVersion.txt' 'm_EditorVersion: 2019.4.40f1'
F 'Projects\orbit-game\Assets\Art\planets.bin' 140 60 9
F 'Projects\orbit-game\Library\ArtifactDB.bin' 610 30
F 'Projects\orbit-game\Library\ShaderCache\shaders.bin' 230 30
F 'Projects\orbit-game\Temp\bake.bin' 48 30
Marker 'Projects\budget-api\BudgetApi.csproj' '<Project />'
F 'Projects\budget-api\bin\Debug\api.bin' 96 15
F 'Projects\budget-api\obj\Debug\api.bin' 70 15
Marker 'Projects\notes-site\pyproject.toml' '[project]'
F 'Projects\notes-site\.venv\Lib\site-packages\libs.bin' 260 40

# Media and documents (personal: never suggested, but big)
F 'Media\Videos\trip-2025.bin' 900 120 11
F 'Media\Videos\trip-2025 (1).bin' 900 20 11
F 'Media\Music\live-set.bin' 240 200 13
F 'Media\Photos\raw\batch-01.bin' 380 90 15
F 'Documents\Scans\archive.bin' 120 300 17

# Downloads: an archive next to its extracted folder, an old archive, a finished file next to a partial one
F 'Downloads\sample-pack\kick.bin' 70 40 19
F 'Downloads\sample-pack\snare.bin' 60 40 21
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $Root 'Downloads\sample-pack'), (Join-Path $Root 'Downloads\sample-pack.zip'), 'NoCompression', $false)
(Get-Item (Join-Path $Root 'Downloads\sample-pack.zip')).LastWriteTime = (Get-Date).AddDays(-40)
F 'Downloads\dataset-2024.bin' 520 160 23

$env:FERAHLA_HOME = $DemoHome
$env:FERAHLA_LANG = 'en'
& $Exe scan $Root --json | Out-Null
# Something grows before the second scan, so "what changed" has a story.
F 'Projects\orbit-game\Library\ShaderCache\shaders-2.bin' 380 1
F 'Projects\recipe-app\node_modules\framework\next.bin' 120 1
Start-Sleep 2
& $Exe scan $Root --json | Out-Null

# Neutral drive numbers instead of this machine's.
$total = [int64]512 * 1GB
$free = @([int64](48.6 * 1GB), [int64](46.1 * 1GB))
$i = 0
foreach ($scan in Get-ChildItem (Join-Path $DemoHome 'scans') -Filter '*.scan.json.gz' | Sort-Object Name) {
    $in = [IO.Compression.GZipStream]::new([IO.File]::OpenRead($scan.FullName), [IO.Compression.CompressionMode]::Decompress)
    $json = [IO.StreamReader]::new($in, [Text.Encoding]::UTF8).ReadToEnd(); $in.Close()
    $json = [regex]::Replace($json, '"totalBytes":\d+', '"totalBytes":' + $total)
    $json = [regex]::Replace($json, '"freeBytes":\d+', '"freeBytes":' + $free[[Math]::Min($i, 1)])
    $out = [IO.Compression.GZipStream]::new([IO.File]::Create($scan.FullName), [IO.Compression.CompressionLevel]::Optimal)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $out.Write($bytes, 0, $bytes.Length); $out.Close()
    Remove-Item ($scan.FullName -replace '\.scan\.json\.gz$', '.meta.json') -ErrorAction SilentlyContinue
    $i++
}
Write-Host "Demo ready. Root: $Root  Home: $DemoHome"
