<#
.SYNOPSIS
    One-time setup: fills in your name and GitHub user, and creates the git repository.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
#>
param(
    [string]$GitHubUser = (Read-Host 'GitHub username (owner of the GoblinTweaks repository)'),
    [string]$Author = (Read-Host 'Your name (author shown in the plugin installer and LICENSE)')
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$files = 'README.md', 'LICENSE', 'GoblinTweaks/GoblinTweaks.json', 'GoblinTweaks/PluginInfo.cs'
foreach ($file in $files) {
    $content = Get-Content $file -Raw -Encoding utf8
    $updated = $content.Replace('GITHUB_USER', $GitHubUser).Replace('GOBLIN_AUTHOR', $Author)
    if ($updated -ne $content) {
        [IO.File]::WriteAllText((Resolve-Path $file), $updated, [Text.UTF8Encoding]::new($false))
    }
}

if (-not (Test-Path '.git')) {
    git init -b main | Out-Null
    git add -A
    git commit --quiet -m 'Initial commit'
    Write-Host 'Git repository created.'
}

Write-Host ''
Write-Host 'Done. Build with: dotnet build -c Release' -ForegroundColor Green
