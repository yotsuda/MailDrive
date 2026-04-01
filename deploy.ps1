#Requires -Version 7.4
<#
.SYNOPSIS
    Build and deploy MailDrive module to the local PowerShell Modules directory.
.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release.
.PARAMETER ModulePath
    Deployment target directory. Default: $env:ProgramFiles\PowerShell\7\Modules\MailDrive
.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ModulePath = "$env:ProgramFiles\PowerShell\7\Modules\MailDrive"
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$srcProject = Join-Path $projectDir 'src\MailDrive\MailDrive.csproj'
$moduleSource = Join-Path $projectDir 'module'

# Build
Write-Host "Building MailDrive ($Configuration)..." -ForegroundColor Cyan
dotnet build $srcProject -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# Prepare output directory
if (Test-Path $ModulePath) {
    Remove-Item "$ModulePath\*" -Recurse -Force
} else {
    New-Item -Path $ModulePath -ItemType Directory -Force | Out-Null
}

# Copy module manifest and format file
Copy-Item (Join-Path $moduleSource 'MailDrive.psd1')          $ModulePath
Copy-Item (Join-Path $moduleSource 'MailDrive.Format.ps1xml') $ModulePath

# Copy built DLL and dependencies
$buildOutput = Join-Path $projectDir "src\MailDrive\bin\$Configuration\net9.0"
@(
    'MailDrive.dll'
    'MailKit.dll'
    'MimeKit.dll'
    'BouncyCastle.Cryptography.dll'
    'Microsoft.Identity.Client.dll'
) | ForEach-Object {
    $src = Join-Path $buildOutput $_
    if (Test-Path $src) { Copy-Item $src $ModulePath }
}

Write-Host "Deployed to $ModulePath" -ForegroundColor Green
Get-ChildItem $ModulePath | Format-Table Name, Length -AutoSize
