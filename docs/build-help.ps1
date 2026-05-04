#Requires -Version 7.4
<#
.SYNOPSIS
    Regenerate PlatyPS markdown help and compile to MAML.
.DESCRIPTION
    For each locale folder under docs/help/, this script:
    1) Updates per-cmdlet markdown from the live module (preserves prose,
       refreshes parameter signatures) via Update-MarkdownHelp.
    2) Patches the per-file "online version" URL to its GitHub blob path.
    3) Compiles the markdown to MAML at the locale-specific module path.
.PARAMETER ModulePath
    Path to the staged module folder (the one that contains MailDrive.psd1).
    Default: $env:TEMP\MailDrive-stage-v030\MailDrive
.PARAMETER Locales
    Locale folder names under docs/help/. Default: en-US.
.EXAMPLE
    .\docs\build-help.ps1
    .\docs\build-help.ps1 -Locales en-US, ja-JP
#>
param(
    [string]$ModulePath = "$env:TEMP\MailDrive-stage-v030\MailDrive",
    [string[]]$Locales = @('en-US')
)

$ErrorActionPreference = 'Stop'
Import-Module platyPS

$repoRoot = Split-Path -Parent $PSScriptRoot
$helpRoot = Join-Path $repoRoot 'docs\help'
$baseUrl  = 'https://github.com/yotsuda/MailDrive/blob/main/docs/help'

# Always import a fresh copy of the staged module so cmdlet metadata is current.
Remove-Module MailDrive -Force -ErrorAction SilentlyContinue
Import-Module (Join-Path $ModulePath 'MailDrive.psd1') -Force

foreach ($locale in $Locales) {
    $mdDir = Join-Path $helpRoot $locale
    if (-not (Test-Path $mdDir)) {
        Write-Host "Bootstrapping $locale ..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Path $mdDir -Force | Out-Null
        $null = New-MarkdownHelp -Module MailDrive `
            -OutputFolder $mdDir -Force -WithModulePage `
            -Locale $locale `
            -HelpVersion (Get-Module MailDrive).Version `
            -FwLink "$baseUrl/$locale/MailDrive.md" `
            -ModulePagePath (Join-Path $mdDir 'MailDrive.md')
    } else {
        Write-Host "Updating $locale ..." -ForegroundColor Cyan
        $null = Update-MarkdownHelp $mdDir
    }

    # Patch per-cmdlet online-version URLs.
    Get-ChildItem $mdDir -Filter '*.md' |
        Where-Object Name -ne 'MailDrive.md' |
        ForEach-Object {
            $text = Get-Content $_.FullName -Raw
            $url  = "$baseUrl/$locale/$($_.Name)"
            $patched = $text -replace '(?m)^online version:.*$', "online version: $url"
            [System.IO.File]::WriteAllText($_.FullName, $patched, [System.Text.UTF8Encoding]::new($false))
        }

    # Compile MAML into <ModulePath>/<locale>/<Module>.dll-Help.xml.
    $mamlDir = Join-Path $ModulePath $locale
    if (-not (Test-Path $mamlDir)) { New-Item -ItemType Directory -Path $mamlDir -Force | Out-Null }
    $null = New-ExternalHelp -Path $mdDir -OutputPath $mamlDir -Force
    Write-Host "  MAML -> $mamlDir\MailDrive.dll-Help.xml" -ForegroundColor Green
}
