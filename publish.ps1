# Publishes the self-contained single-file build.
# Pure ASCII on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 as the OEM code page,
# so non-ASCII text in this file would be mangled. Pass -OutDir to choose the target folder.
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Gpd.App\Gpd.App.csproj'

if (-not $OutDir) {
    $OutDir = Join-Path (Split-Path -Parent $root) 'publish'
}

Write-Host "project : $project"
Write-Host "out dir : $OutDir"

# Publish into an EXISTING folder drops content files: the SDK's incremental cleanup removes
# QuestPDF.Fonts.Lato.br from the target but then skips re-copying it as "up to date".
# Result: a folder with only the exe, whose PDF output silently fails. Always publish clean.
if (Test-Path $OutDir) {
    Write-Host 'cleaning previous output...'
    Remove-Item $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $OutDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ''
Write-Host 'Published files:'
Get-ChildItem $OutDir | ForEach-Object { Write-Host ("  {0,-40} {1,12:N0} bytes" -f $_.Name, $_.Length) }

# Verify the output is actually runnable, not just non-empty.
$exe = Join-Path $OutDir 'wiisl.exe'
$font = Join-Path $OutDir 'QuestPDF.Fonts.Lato.br'

$missing = @()
if (-not (Test-Path $exe)) { $missing += 'wiisl.exe' }
if (-not (Test-Path $font)) { $missing += 'QuestPDF.Fonts.Lato.br' }

if ($missing.Count -gt 0) {
    throw ("publish output is incomplete, missing: " + ($missing -join ', ') +
           ". Without QuestPDF.Fonts.Lato.br the exe cannot generate PDF reports.")
}

Write-Host ''
Write-Host 'OK: both required files are present.'
Write-Host 'IMPORTANT: keep BOTH files together in the same folder.'
Write-Host 'wiisl.exe needs QuestPDF.Fonts.Lato.br next to it to render PDF reports.'
