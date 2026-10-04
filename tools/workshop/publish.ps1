<#
.SYNOPSIS
  Builds and tests RimMult, packages the mod folder and uploads it to its Steam Workshop item
  (mod/About/PublishedFileId.txt) through the running Steam client: no password, no steamcmd.
.EXAMPLE
  ./tools/workshop/publish.ps1 -Note "Fixes …" -NoteRu "Исправлено …"   # change note in English and Russian
  ./tools/workshop/publish.ps1 -Description                              # also the page's description (description.bbcode)
  ./tools/workshop/publish.ps1 -DescriptionOnly                          # only the description: no build, no files
  ./tools/workshop/publish.ps1 -Check                                    # everything but the upload (checks Steam too)
#>
param(
    [string]$Note,
    [string]$NoteRu,
    [switch]$Description,
    [switch]$DescriptionOnly,
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

$item = (Get-Content (Join-Path $root 'mod\About\PublishedFileId.txt') -Raw).Trim()
$tool = Join-Path $root 'tools\WorkshopUpload\bin\Release\net10.0\win-x64\WorkshopUpload.exe'
$descriptionFile = Join-Path $PSScriptRoot 'description.bbcode'
if (-not $Note) {
    $version = ([xml](Get-Content (Join-Path $root 'mod\About\About.xml') -Raw)).ModMetaData.modVersion
    $Note = "$version ($(git rev-parse --short HEAD)): $(git log -1 --no-merges --format=%s)"
}
# Players read the change notes in both languages: English first, then Russian.
if ($NoteRu) { $Note = "$Note`n`n$NoteRu" }

if ($DescriptionOnly) {
    Run 'dotnet' @('build', 'tools/WorkshopUpload', '-c', 'Release', '--nologo', '-v', 'q', "-p:RimWorldDir=$RimWorldDir")
    Step "Description of Workshop item $item"
    if ($Check) { Run $tool @('--check'); Write-Host "Would set the description from $descriptionFile"; return }
    Run $tool @('--item', $item, '--description', $descriptionFile, '--note', $Note)
    return
}

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

if ($Check) {
    Step 'Steam check (nothing is uploaded)'
    Run $tool @('--check')
    Write-Host "Would upload $stage to Workshop item $item$(if ($Description) { ' with the description' }) and the note:`n$Note"
    return
}

Step "Upload to Workshop item $item"
# Steam first fetches the previous version's manifest from its CDN, which now and then fails for a moment
# (k_EResultFail, "Failed to download manifest" in Steam/logs/workshop_log.txt): try again before giving up.
$uploadArgs = @('--item', $item, '--content', $stage, '--preview', (Join-Path $stage 'About\Preview.png'), '--note', $Note)
if ($Description) { $uploadArgs += @('--description', $descriptionFile) }
for ($try = 1; ; $try++) {
    & $tool @uploadArgs
    if ($LASTEXITCODE -eq 0) { break }
    if ($LASTEXITCODE -ne 3 -or $try -ge 3) { throw "Upload failed (exit $LASTEXITCODE); see Steam/logs/workshop_log.txt" }
    Write-Warning "Upload attempt $try failed; trying again in 15 s."
    Start-Sleep -Seconds 15
}

# Steam may keep the subscribed copy on the old version for hours (even across a game start): fetch it now.
Step 'Update the subscribed copy on this PC'
Run $tool @('--download', $item)
