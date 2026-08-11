[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$DisableNuGetAudit,
    [string]$Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$artifacts = Join-Path $repo 'artifacts'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$canonicalLicenseSha256 = '007F4954B08C74FB03505BD591239C614EA48B7F714CCDD8F32D5D7A7E2B57EC'
$manualFileName = 'README_' + [string][char]0x64CD + [string][char]0x4F5C + [string][char]0x624B + [string][char]0x518A + '.txt'
$manualRuntimeRelativePath = Join-Path 'Program\Docs' $manualFileName

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
        throw "Reparse point refused: $Path"
    }
    $nested = Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $nested) { throw "Nested reparse point refused: $($nested.FullName)" }
}

function Reset-ReleaseDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = [IO.Path]::GetFullPath((Join-Path $artifacts 'release')).TrimEnd('\') + '\'
    if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release cleanup path: $full"
    }
    if (Test-Path -LiteralPath $full) {
        Assert-NoReparsePoint $full
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
    $full
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Source directory missing: $Source" }
    Assert-NoReparsePoint $Source
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Copy-RequiredFile([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Required release source missing: $Source" }
    $parent = Split-Path -Parent $Destination
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Assert-PublicManual([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { throw "$Label is empty: $Path" }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "$Label must be UTF-8 without BOM: $Path"
    }
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    try {
        $text = $strictUtf8.GetString($bytes)
    }
    catch [Text.DecoderFallbackException] {
        throw "$Label is not strict UTF-8: $Path"
    }
    if ([string]::IsNullOrWhiteSpace($text)) { throw "$Label contains no usable text: $Path" }
    if ($text -match '(?<![A-Za-z])[A-Za-z]:[\\/]' -or
        $text -match '(?i)(?:/Users/|/home/|\.codex(?:[/\\]|\b)|OwnerOnly)' -or
        $text -match '(?i)(github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16}|AIza[0-9A-Za-z_-]{20,}|xox[baprs]-[0-9A-Za-z-]{10,}|BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY)' -or
        $text -match '(?i)\b(api[_-]?key|client[_-]?secret|access[_-]?token|password)\b\s*[:=]\s*\S{8,}') {
        throw "$Label contains a private path or secret-like value: $Path"
    }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Utf8NoBom([string]$Path, [string[]]$Lines) {
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllLines($Path, $Lines, $script:utf8NoBom)
}

function Get-DotNetLegalFiles([string]$DotNetExe) {
    $repositoryLegal = Join-Path $repo 'legal'
    $sdkRoot = Split-Path -Parent $DotNetExe
    $candidates = @(
        [pscustomobject]@{
            License = Join-Path $repositoryLegal 'DOTNET_LICENSE.txt'
            Notices = Join-Path $repositoryLegal 'DOTNET_THIRD_PARTY_NOTICES.txt'
        },
        [pscustomobject]@{
            License = Join-Path $sdkRoot 'LICENSE.txt'
            Notices = Join-Path $sdkRoot 'ThirdPartyNotices.txt'
        }
    )
    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath $candidate.License -PathType Leaf) -and
            (Test-Path -LiteralPath $candidate.Notices -PathType Leaf)) {
            return $candidate
        }
    }
    throw 'Official .NET LICENSE.txt and ThirdPartyNotices.txt were not found in the repository or beside dotnet.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $Version" }
$licensePath = Join-Path $repo 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "Canonical repository LICENSE is missing: $licensePath"
}
$licenseHash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($licenseHash -ne $canonicalLicenseSha256) {
    throw "Canonical repository LICENSE hash mismatch: $licenseHash"
}
$canonicalManualPath = Join-Path $repo $manualFileName
$canonicalManualHash = Assert-PublicManual $canonicalManualPath 'Canonical repository operation manual'
Set-RepositoryEnvironment
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$test = Join-Path $PSScriptRoot 'Test.ps1'
$publish = Join-Path $PSScriptRoot 'Publish.ps1'
$testParameters = @{}
$publishParameters = @{}
if ($DisableNuGetAudit) {
    $testParameters.DisableNuGetAudit = $true
    $publishParameters.DisableNuGetAudit = $true
}
if ($SelfContained) { $publishParameters.SelfContained = $true }

& $test @testParameters
& $publish @publishParameters

$flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$publishRoot = Join-Path $artifacts "publish\$flavor"
$releaseParent = Join-Path $artifacts 'release'
New-Item -ItemType Directory -Path $releaseParent -Force | Out-Null
$release = Reset-ReleaseDirectory (Join-Path $releaseParent "MouseKeyboardMacro-v$Version-$flavor")

$launcherSource = Join-Path $repo 'scripts\portable-launchers'
$launchers = @(Get-ChildItem -LiteralPath $launcherSource -Filter '*.cmd' -File -ErrorAction Stop | Sort-Object Name)
if ($launchers.Count -ne 5) { throw "Expected exactly five portable launchers; found $($launchers.Count)." }
foreach ($launcher in $launchers) {
    Copy-RequiredFile $launcher.FullName (Join-Path $release $launcher.Name)
}

$appFolders = [ordered]@{
    MacroLauncher = 'Launcher'
    MacroRecorder = 'Recorder'
    MacroPlayer = 'Player'
    MacroSafetyWatchdog = 'Watchdog'
}
foreach ($name in $appFolders.Keys) {
    $source = Join-Path $publishRoot $name
    $destination = Join-Path $release "Program\App\$($appFolders[$name])"
    Copy-DirectoryContents $source $destination
    Get-ChildItem -LiteralPath $destination -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force
    if (-not (Test-Path -LiteralPath (Join-Path $destination "$name.exe") -PathType Leaf)) {
        throw "Release executable missing: $name"
    }
}

$rootDocuments = @(
    'README.md', 'INSTALL.md', 'USER_GUIDE.md', 'BUILDING.md', 'TROUBLESHOOTING.md',
    'SECURITY.md', 'PRIVACY.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'LICENSE_STATUS.md',
    $manualFileName
)
foreach ($name in $rootDocuments) {
    Copy-RequiredFile (Join-Path $repo $name) (Join-Path $release $name)
}
$supportDocuments = @('FAQ.md', 'INPUT_MODES.md', 'SECURITY_MODEL.md')
foreach ($name in $supportDocuments) {
    Copy-RequiredFile (Join-Path $repo "docs\$name") (Join-Path $release "docs\$name")
}
Copy-RequiredFile (Join-Path $repo 'START_HERE.txt') (Join-Path $release 'START_HERE.txt')
Copy-RequiredFile (Join-Path $repo 'THIRD_PARTY_NOTICES.md') (Join-Path $release 'THIRD_PARTY_NOTICES.txt')

$imageNames = @(
    'recorder-standard.png', 'recorder-raw.png', 'player-main.png',
    'player-mouse-modes.png', 'player-keep-visible.png'
)
$releaseImages = Join-Path $release 'docs\images'
New-Item -ItemType Directory -Path $releaseImages -Force | Out-Null
foreach ($name in $imageNames) {
    Copy-RequiredFile (Join-Path $repo "docs\images\$name") (Join-Path $releaseImages $name)
}

$legal = Get-DotNetLegalFiles $dotnet
Copy-RequiredFile $legal.License (Join-Path $release 'DOTNET_LICENSE.txt')
Copy-RequiredFile $legal.Notices (Join-Path $release 'DOTNET_THIRD_PARTY_NOTICES.txt')

foreach ($relative in @('Program\State\Logs', 'Program\State\Settings', 'Recordings')) {
    New-Item -ItemType Directory -Path (Join-Path $release $relative) -Force | Out-Null
}
Write-Utf8NoBom (Join-Path $release 'Program\project-root.marker') @('MOUSE_KEYBOARD_MACRO_ROOT_V2')
$releaseRootManual = Join-Path $release $manualFileName
$releaseRuntimeManual = Join-Path $release $manualRuntimeRelativePath
Copy-RequiredFile $releaseRootManual $releaseRuntimeManual
$releaseRootManualHash = Assert-PublicManual $releaseRootManual 'Release-root operation manual'
$releaseRuntimeManualHash = Assert-PublicManual $releaseRuntimeManual 'Runtime operation manual'
if ($releaseRootManualHash -ne $canonicalManualHash -or $releaseRuntimeManualHash -ne $canonicalManualHash) {
    throw "Release operation manuals are not byte-identical to the canonical repository manual. Canonical=$canonicalManualHash Root=$releaseRootManualHash Runtime=$releaseRuntimeManualHash"
}

$releaseLicensePath = Join-Path $release 'LICENSE'
Copy-RequiredFile $licensePath $releaseLicensePath
$releaseLicenseHash = (Get-FileHash -LiteralPath $releaseLicensePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($releaseLicenseHash -ne $licenseHash) {
    throw "Release LICENSE was not copied byte-for-byte: $releaseLicenseHash"
}
Write-Utf8NoBom (Join-Path $release 'LICENSE_STATUS.txt') @(
    'LICENSE_INCLUDED',
    'SPDX_IDENTIFIER=MIT',
    'Copyright (c) 2026 ru'
)

Push-Location -LiteralPath $repo
try {
    $sdkVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to query the .NET SDK version.' }
}
finally {
    Pop-Location
}
Write-Utf8NoBom (Join-Path $release 'PORTABLE_RELEASE.txt') @(
    'PORTABLE_RELEASE=TRUE',
    "PRODUCT=MouseKeyboardMacro",
    "VERSION=$Version",
    'RUNTIME_IDENTIFIER=win-x64',
    "FLAVOR=$flavor",
    'LICENSE_INCLUDED',
    'SPDX_IDENTIFIER=MIT',
    'Copyright (c) 2026 ru',
    'LIVE_INPUT_EXECUTED=NO',
    'REGISTRY_MODIFIED=NO',
    'UAC_REQUESTED=NO'
)
Write-Utf8NoBom (Join-Path $release 'BUILD_INFO.txt') @(
    "VERSION=$Version",
    'RUNTIME_IDENTIFIER=win-x64',
    "FLAVOR=$flavor",
    "DOTNET_SDK=$sdkVersion",
    'CONTINUOUS_INTEGRATION_BUILD=TRUE',
    'LICENSE_INCLUDED',
    'SPDX_IDENTIFIER=MIT',
    'Copyright (c) 2026 ru'
)

$appRoot = Join-Path $release 'Program\App'
$binaryLines = @(Get-ChildItem -LiteralPath $appRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($release.Length + 1).Replace('\', '/')
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $relative"
})
if ($binaryLines.Count -eq 0) { throw 'Release binary manifest would be empty.' }
Write-Utf8NoBom (Join-Path $release 'BINARY_SHA256SUMS.txt') $binaryLines
Write-Utf8NoBom (Join-Path $release 'RELEASE_BINARY_MANIFEST.sha256') $binaryLines

$verify = Join-Path $PSScriptRoot 'Verify-Release.ps1'
$verifyReport = Join-Path $artifacts "verify-release-$flavor.txt"
& $verify -ReleaseRoot $release -ExpectedFlavor $flavor -ReportPath $verifyReport

Write-Output "PORTABLE_RELEASE=PASS|FLAVOR=$flavor|OUTPUT=$release|VERIFY_REPORT=$verifyReport"
