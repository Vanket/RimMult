<#
.SYNOPSIS
  Builds and tests RimMult, packages the mod folder and uploads it to its Steam Workshop item
  (mod/About/PublishedFileId.txt) through the running Steam client: no password, no steamcmd.
.EXAMPLE
  ./tools/workshop/publish.ps1                   # change note from the last commit
  ./tools/workshop/publish.ps1 -Note "Fixes …"   # own change note
  ./tools/workshop/publish.ps1 -Check            # everything but the upload (checks Steam too)
#>
param(
    [string]$Note,
    [switch]$Check,
    [string]$RimWorldDir = 'D:\SteamLibrary\steamapps\common\RimWorld'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }
function Run([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
}

if (git status --porcelain) { Write-Warning 'Uncommitted changes: they go into the upload too.' }
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { Write-Warning "Uploading from branch '$branch', not main." }

Step 'Build'
Run 'dotnet' @('build', '-c', 'Release', '--nologo', '-v', 'q')
Step 'Test'
Run 'dotnet' @('test', '-c', 'Release', '--no-build')
Run 'dotnet' @('build', 'tools/WorkshopUpload', '-c', 'Release', '--nologo', '-v', 'q', "-p:RimWorldDir=$RimWorldDir")

Step 'Package'
$stage = Join-Path ([IO.Path]::GetTempPath()) 'RimMult-workshop\RimMult'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $root 'mod\*') $stage -Recurse
Get-ChildItem $stage -Recurse -Force -Include '.gitkeep', '*.pdb' | Remove-Item -Force

$item = (Get-Content (Join-Path $root 'mod\About\PublishedFileId.txt') -Raw).Trim()
if (-not $Note) {
    $version = ([xml](Get-Content (Join-Path $root 'mod\About\About.xml') -Raw)).ModMetaData.modVersion
    $Note = "$version ($(git rev-parse --short HEAD)): $(git log -1 --no-merges --format=%s)"
}
$tool = Join-Path $root 'tools\WorkshopUpload\bin\Release\net10.0\win-x64\WorkshopUpload.exe'

if ($Check) {
    Step 'Steam check (nothing is uploaded)'
    Run $tool @('--check')
    Write-Host "Would upload $stage to Workshop item $item with the note: $Note"
    return
}

Step "Upload to Workshop item $item"
Run $tool @('--item', $item, '--content', $stage, '--preview', (Join-Path $stage 'About\Preview.png'), '--note', $Note)
