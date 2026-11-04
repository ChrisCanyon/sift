# Builds Sift, installs (or updates) the global `sift` tool, and installs the /sift skill for Claude Code.
# -LinkSkill links the skill to this clone instead of copying it, so edits here apply immediately.
param([switch]$LinkSkill)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'

dotnet pack (Join-Path $repoRoot 'src/Sift') -c Release -o $artifacts
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }

# Run from the repo so its nuget.config (nuget.org only) applies.
Push-Location $repoRoot
try {
    # 'dotnet tool update' skips same-version packages, so a rebuilt 0.1.0 would never land.
    $installed = dotnet tool list -g | Select-String -SimpleMatch 'sift.review'
    if ($installed) {
        dotnet tool uninstall -g Sift.Review | Out-Null
    }
    dotnet tool install -g Sift.Review --add-source $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install failed' }
}
finally {
    Pop-Location
}

$skillSource = Join-Path $repoRoot 'skills/sift'
$skillTarget = Join-Path $HOME '.claude/skills/sift'
New-Item -ItemType Directory -Force (Split-Path $skillTarget) | Out-Null

$existing = Get-Item $skillTarget -ErrorAction SilentlyContinue
if ($existing -and $existing.LinkType) {
    $existing.Delete()
}
elseif ($existing) {
    Remove-Item $skillTarget -Recurse -Force
}

if ($LinkSkill) {
    $linkType = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $skillTarget -Target $skillSource | Out-Null
}
else {
    Copy-Item $skillSource $skillTarget -Recurse
}

Write-Host "Installed: sift, skill at $skillTarget"
