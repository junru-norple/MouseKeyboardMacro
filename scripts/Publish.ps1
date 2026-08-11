[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$DisableNuGetAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$artifacts = Join-Path $repo 'artifacts'

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
    foreach ($directory in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:TEMP, $artifacts)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

function Assert-NoReparsePoint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing reparse-point publish cleanup: $Path"
    }
    $nested = Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $nested) { throw "Refusing nested reparse point: $($nested.FullName)" }
}

function Reset-PublishDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
    if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe publish cleanup path: $full"
    }
    if (Test-Path -LiteralPath $full) {
        Assert-NoReparsePoint $full
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
    $full
}

function Invoke-DotNet([string]$DotNetExe, [string[]]$Arguments) {
    & $DotNetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Set-RepositoryEnvironment
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$solution = Join-Path $repo 'MouseKeyboardMacro.sln'
$flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$selfValue = if ($SelfContained) { 'true' } else { 'false' }
$publishRoot = Reset-PublishDirectory (Join-Path $artifacts "publish\$flavor")

Push-Location -LiteralPath $repo
try {
    $restore = @(
        'restore', $solution,
        '--runtime', 'win-x64',
        '--disable-parallel',
        '-p:RestoreDisableParallel=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )
    if ($DisableNuGetAudit) { $restore += '-p:NuGetAudit=false' }
    Invoke-DotNet $dotnet $restore

    foreach ($name in @('MacroLauncher', 'MacroRecorder', 'MacroPlayer', 'MacroSafetyWatchdog')) {
        $project = Join-Path $repo "src\$name\$name.csproj"
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Public app project missing: $project" }
        $output = Join-Path $publishRoot $name
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Invoke-DotNet $dotnet @(
            'publish', $project,
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained', $selfValue,
            '--no-restore',
            '-m:1',
            '-o', $output,
            '-p:BuildInParallel=false',
            '-p:UseSharedCompilation=false',
            '-p:NodeReuse=false',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:IncludeSourceRevisionInInformationalVersion=false',
            '-p:ContinuousIntegrationBuild=true'
        )
        Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force
        if (-not (Test-Path -LiteralPath (Join-Path $output "$name.exe") -PathType Leaf)) {
            throw "Published executable missing: $name"
        }
    }
    Write-Output "PUBLIC_PUBLISH=PASS|FLAVOR=$flavor|OUTPUT=$publishRoot|LIVE_INPUT=NO"
}
finally {
    Pop-Location
}
