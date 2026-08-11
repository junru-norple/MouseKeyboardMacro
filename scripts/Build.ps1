[CmdletBinding()]
param(
    [string]$SolutionPath,
    [switch]$DisableNuGetAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')

function Resolve-RepositoryFile([string]$Path, [string]$DefaultRelativePath) {
    $candidate = if ([string]::IsNullOrWhiteSpace($Path)) {
        Join-Path $repo $DefaultRelativePath
    }
    elseif ([IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $repo $Path
    }
    $full = [IO.Path]::GetFullPath($candidate)
    if (-not $full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build input is outside the repository: $full"
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Build input is missing: $full"
    }
    $full
}

function Set-RepositoryEnvironment {
    $env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-cli-home'
    $env:NUGET_PACKAGES = Join-Path $repo '.nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.nuget-http-cache'
    $env:TEMP = Join-Path $repo 'temp'
    $env:TMP = $env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_XMLDOC_MODE = 'skip'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:MKM_SAFE_VALIDATION_MODE = '1'
    foreach ($directory in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:TEMP)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

function Invoke-DotNet([string]$DotNetExe, [string[]]$Arguments) {
    & $DotNetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$solution = Resolve-RepositoryFile $SolutionPath 'MouseKeyboardMacro.sln'
Set-RepositoryEnvironment
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

Push-Location -LiteralPath $repo
try {
    $sdkVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^8\.0\.4\d{2}$') {
        throw "The pinned .NET 8.0.4xx SDK is required; resolved '$sdkVersion'."
    }

    $restore = @(
        'restore', $solution,
        '--disable-parallel',
        '-p:RestoreDisableParallel=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )
    if ($DisableNuGetAudit) { $restore += '-p:NuGetAudit=false' }
    Invoke-DotNet $dotnet $restore

    Invoke-DotNet $dotnet @(
        'build', $solution,
        '-c', 'Release',
        '--no-restore',
        '-m:1',
        '-warnaserror',
        '-p:BuildInParallel=false',
        '-p:UseSharedCompilation=false',
        '-p:NodeReuse=false',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:ContinuousIntegrationBuild=true'
    )
    Write-Output "PUBLIC_BUILD=PASS|SDK=$sdkVersion|SOLUTION=$([IO.Path]::GetFileName($solution))"
}
finally {
    Pop-Location
}
