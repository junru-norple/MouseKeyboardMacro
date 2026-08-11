[CmdletBinding()]
param([switch]$IncludeCaches)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')

function Assert-RepositoryChild([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $full.StartsWith($repo + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Outside repository: $full" }
    $full
}

function Assert-NoReparsePoint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $found = Get-Item -LiteralPath $Path -Force
    if (($found.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused: $Path" }
    $nested = Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
    if ($null -ne $nested) { throw "Nested reparse point refused: $($nested.FullName)" }
}

Push-Location -LiteralPath $repo
try {
    $names = @('TestSandbox','TestResults','artifacts','temp')
    if ($IncludeCaches) { $names += @('.dotnet-cli-home','.nuget-packages','.nuget-http-cache') }
    $targets = New-Object System.Collections.Generic.List[string]
    foreach ($name in $names) { $targets.Add((Join-Path $repo $name)) }
    foreach ($base in @('src','tests','tools')) {
        $basePath = Join-Path $repo $base
        if (Test-Path -LiteralPath $basePath) {
            foreach ($directory in Get-ChildItem -LiteralPath $basePath -Directory -Force -Recurse | Where-Object { $_.Name -in @('bin','obj') } | Sort-Object { $_.FullName.Length } -Descending) { $targets.Add($directory.FullName) }
        }
    }
    foreach ($target in $targets | Select-Object -Unique) {
        $safe = Assert-RepositoryChild $target
        if (Test-Path -LiteralPath $safe) { Assert-NoReparsePoint $safe; Remove-Item -LiteralPath $safe -Force -Recurse }
    }
    Write-Output "CLEAN_RESULT=PASS|CACHES=$(if ($IncludeCaches) { 'REMOVED' } else { 'PRESERVED' })|USER_DATA=UNTOUCHED"
}
finally {
    Pop-Location
}
