[CmdletBinding()]
param(
    [string]$SolutionPath,
    [string]$TestProjectPath,
    [ValidateRange(1, 100000)]
    [int]$ExpectedTestCount = 880,
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
        throw "Test input is outside the repository: $full"
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Test input is missing: $full"
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
    foreach ($directory in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:TEMP, (Join-Path $repo 'TestSandbox'), (Join-Path $repo 'TestResults'))) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

function Read-TrxCounters([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Public test TRX is missing: $Path" }
    $document = New-Object System.Xml.XmlDocument
    $document.Load($Path)
    $node = $document.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $node) { throw "Public test TRX has no Counters element: $Path" }
    [pscustomobject]@{
        Total = [int]$node.GetAttribute('total')
        Passed = [int]$node.GetAttribute('passed')
        Failed = [int]$node.GetAttribute('failed')
        Skipped = [int]$node.GetAttribute('notExecuted')
    }
}

function Get-ProtectedRuntimeSnapshot([string]$Root) {
    $snapshot = [ordered]@{}
    foreach ($relativeRoot in @('Program\State', 'Recordings')) {
        $absoluteRoot = Join-Path $Root $relativeRoot
        $normalizedRoot = $relativeRoot.Replace('\', '/')
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
            $snapshot["M|$normalizedRoot"] = ''
            continue
        }
        $rootItem = Get-Item -LiteralPath $absoluteRoot -Force
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Protected runtime root is a reparse point: $absoluteRoot"
        }
        $snapshot["D|$normalizedRoot"] = ''
        foreach ($directory in Get-ChildItem -LiteralPath $absoluteRoot -Directory -Force -Recurse | Sort-Object FullName) {
            if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Protected runtime tree contains a reparse point: $($directory.FullName)"
            }
            $relative = $directory.FullName.Substring($Root.Length + 1).Replace('\', '/')
            $snapshot["D|$relative"] = ''
        }
        foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -File -Force -Recurse | Sort-Object FullName) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Protected runtime tree contains a reparse-point file: $($file.FullName)"
            }
            $relative = $file.FullName.Substring($Root.Length + 1).Replace('\', '/')
            $snapshot["F|$relative"] = "$($file.Length)|$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)"
        }
    }
    $snapshot
}

function Assert-ProtectedRuntimeSnapshot($Before, $After, [string]$Label) {
    $differences = [Collections.Generic.List[string]]::new()
    foreach ($key in @($Before.Keys + $After.Keys | Sort-Object -Unique)) {
        if (-not $Before.Contains($key)) { $differences.Add("ADDED:$key"); continue }
        if (-not $After.Contains($key)) { $differences.Add("MISSING:$key"); continue }
        if ($Before[$key] -ne $After[$key]) { $differences.Add("CHANGED:$key") }
    }
    if ($differences.Count -ne 0) {
        throw "$Label modified protected Program/State or Recordings: $($differences -join ';')"
    }
    Write-Output "PRODUCTION_STATE_PRE_POST_PARITY=PASS|GATE=$Label|ENTRIES=$($Before.Count)"
}

function Remove-TestRuntimeRoot([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo 'TestSandbox')).TrimEnd('\') + '\'
    if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing test runtime cleanup outside TestSandbox: $full"
    }
    $items = @(Get-Item -LiteralPath $full -Force) + @(Get-ChildItem -LiteralPath $full -Force -Recurse -ErrorAction Stop)
    $reparse = $items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
    if ($null -ne $reparse) { throw "Refusing reparse-point test runtime cleanup: $($reparse.FullName)" }
    Remove-Item -LiteralPath $full -Recurse -Force
}

$solution = Resolve-RepositoryFile $SolutionPath 'MouseKeyboardMacro.sln'
$testProject = Resolve-RepositoryFile $TestProjectPath 'tests\MacroCore.Tests\MacroCore.Tests.csproj'
$build = Join-Path $PSScriptRoot 'Build.ps1'
$buildParameters = @{ SolutionPath = $solution }
if ($DisableNuGetAudit) { $buildParameters.DisableNuGetAudit = $true }
$protectedStateBefore = Get-ProtectedRuntimeSnapshot $repo
& $build @buildParameters

Set-RepositoryEnvironment
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$testRuntimeRoot = Join-Path $repo ("TestSandbox\public-runtime-$PID-$([Guid]::NewGuid().ToString('N'))")
New-Item -ItemType Directory -Path (Join-Path $testRuntimeRoot 'Program\State\Logs'), (Join-Path $testRuntimeRoot 'Program\State\Settings'), (Join-Path $testRuntimeRoot 'Recordings') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $testRuntimeRoot 'Program\project-root.marker'), "MOUSE_KEYBOARD_MACRO_ROOT_V2`r`n", [Text.Encoding]::ASCII)
$oldProjectRoot = $env:MKM_PROJECT_ROOT
$trxPath = Join-Path $repo 'TestResults\public-tests.trx'
if (Test-Path -LiteralPath $trxPath -PathType Leaf) { Remove-Item -LiteralPath $trxPath -Force }

Push-Location -LiteralPath $repo
try {
    $env:MKM_PROJECT_ROOT = $testRuntimeRoot
    & $dotnet test $testProject -c Release --no-build --no-restore -m:1 `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        --logger 'trx;LogFileName=public-tests.trx' `
        --results-directory (Join-Path $repo 'TestResults')
    if ($LASTEXITCODE -ne 0) {
        throw "Public tests failed with exit code $LASTEXITCODE."
    }
    $counters = Read-TrxCounters $trxPath
    if ($counters.Total -ne $ExpectedTestCount -or $counters.Passed -ne $ExpectedTestCount -or
        $counters.Failed -ne 0 -or $counters.Skipped -ne 0) {
        throw "Public test counters mismatch: total=$($counters.Total), passed=$($counters.Passed), failed=$($counters.Failed), skipped=$($counters.Skipped), expected=$ExpectedTestCount."
    }
    Assert-ProtectedRuntimeSnapshot $protectedStateBefore (Get-ProtectedRuntimeSnapshot $repo) 'PUBLIC_TESTS'
    Write-Output "PUBLIC_TESTS=PASS|TOTAL=$($counters.Total)|PASSED=$($counters.Passed)|FAILED=$($counters.Failed)|SKIPPED=$($counters.Skipped)|PROJECT=$([IO.Path]::GetFileName($testProject))|FILTER=NONE|LIVE_INPUT=NO"
}
finally {
    $env:MKM_PROJECT_ROOT = $oldProjectRoot
    Pop-Location
    Remove-TestRuntimeRoot $testRuntimeRoot
}
