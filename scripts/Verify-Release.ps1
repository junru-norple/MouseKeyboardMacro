[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [string]$ReleaseAssets,
    [string]$ReportPath,
    [ValidateSet('framework-dependent','self-contained')]
    [string]$ExpectedFlavor,
    [switch]$SafeSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$CanonicalLicenseSha256 = '007F4954B08C74FB03505BD591239C614EA48B7F714CCDD8F32D5D7A7E2B57EC'
$manualFileName = 'README_' + [string][char]0x64CD + [string][char]0x4F5C + [string][char]0x624B + [string][char]0x518A + '.txt'
$manualRuntimeRelativePath = Join-Path 'Program\Docs' $manualFileName
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.nuget-http-cache'
$env:TEMP = Join-Path $repo 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MKM_SAFE_VALIDATION_MODE = '1'
$tempParent = Join-Path $repo 'TestSandbox'
$tempParentExisted = Test-Path -LiteralPath $tempParent
$tempRoot = Join-Path $tempParent ("VerifyRelease-" + [guid]::NewGuid().ToString('N'))
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) { $ReleaseRoot = Join-Path $repo 'artifacts\release' }
elseif (-not [IO.Path]::IsPathRooted($ReleaseRoot)) { $ReleaseRoot = Join-Path $repo $ReleaseRoot }
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
if (-not [string]::IsNullOrWhiteSpace($ReleaseAssets)) {
    if (-not [IO.Path]::IsPathRooted($ReleaseAssets)) { $ReleaseAssets = Join-Path $repo $ReleaseAssets }
    $ReleaseAssets = [IO.Path]::GetFullPath($ReleaseAssets)
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Join-Path $repo 'artifacts\verify-release-report.txt' }
elseif (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path $repo $ReportPath }
$report = New-Object System.Collections.Generic.List[string]
$ExpectedImages = @(
    'recorder-standard.png', 'recorder-raw.png', 'player-main.png',
    'player-mouse-modes.png', 'player-keep-visible.png'
)

function Assert-SafeEntry([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Contains('\') -or $Name.StartsWith('/') -or $Name.Contains(':') -or $Name -match '(^|/)\.\.?(/|$)') { throw "Unsafe ZIP entry: $Name" }
    foreach ($segment in $Name.TrimEnd('/').Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '^(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$') { throw "Unsafe ZIP segment: $segment" }
    }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($sha256.ComputeHash($Stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-CanonicalLicense([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($hash -ne $script:CanonicalLicenseSha256) {
        throw "$Label hash mismatch: $hash"
    }
    $hash
}

function Assert-CmdBytes([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw "CMD has UTF-8 BOM: $Path" }
    for ($i=0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 0x7F -or
            ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i-1] -ne 13)) -or
            ($bytes[$i] -eq 13 -and ($i + 1 -ge $bytes.Length -or $bytes[$i+1] -ne 10))) {
            throw "CMD byte contract failed: $Path"
        }
    }
}

function Get-UInt32BigEndian([byte[]]$Bytes, [int]$Offset) {
    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) { throw 'PNG integer is out of range.' }
    [uint32]([Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($Bytes, $Offset)))
}

function Assert-Png([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $signature = [byte[]](137,80,78,71,13,10,26,10)
    if ($bytes.Length -lt 33) { throw "PNG is too small: $Path" }
    for ($index = 0; $index -lt $signature.Length; $index++) {
        if ($bytes[$index] -ne $signature[$index]) { throw "Invalid PNG signature: $Path" }
    }
    $offset = 8
    $width = 0
    $height = 0
    $sawEnd = $false
    while ($offset + 12 -le $bytes.Length) {
        $length = [int](Get-UInt32BigEndian $bytes $offset)
        $type = [Text.Encoding]::ASCII.GetString($bytes, $offset + 4, 4)
        if ($length -lt 0 -or $offset + 12 + $length -gt $bytes.Length) { throw "Malformed PNG chunk: $Path" }
        if ($type -eq 'IHDR') {
            if ($length -ne 13) { throw "Invalid PNG IHDR: $Path" }
            $width = [int](Get-UInt32BigEndian $bytes ($offset + 8))
            $height = [int](Get-UInt32BigEndian $bytes ($offset + 12))
        }
        if ($type -in @('tEXt','zTXt','iTXt','eXIf','tIME')) { throw "PNG metadata chunk is forbidden: $type in $Path" }
        if ($type -eq 'IEND') { $sawEnd = $true; break }
        $offset += 12 + $length
    }
    if (-not $sawEnd -or $width -ne 1280 -or $height -ne 720) {
        throw "PNG contract failed: $Path (${width}x${height}, IEND=$sawEnd)"
    }
}

function Assert-MarkdownLinks([string]$Root) {
    foreach ($file in Get-ChildItem -LiteralPath $Root -Filter '*.md' -File -Recurse) {
        $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
        foreach ($match in [regex]::Matches($text, '!?(?<!\\)\[[^\]]*\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value.Trim().Trim('<','>')
            if ($target -match '^(?i)(https?://|mailto:|#)') { continue }
            $target = ($target -split '#')[0]
            if ([string]::IsNullOrWhiteSpace($target)) { continue }
            $decoded = [Uri]::UnescapeDataString($target).Replace('/', '\')
            $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $decoded))
            $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
            if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $resolved)) {
                throw "Broken Markdown link in $($file.Name): $target"
            }
        }
    }
}

function Assert-ExactNameSet([string[]]$Actual, [string[]]$Expected, [string]$Label) {
    $actualSet = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @($Actual)) {
        if (-not $actualSet.Add($name)) { throw "Duplicate $Label name: $name" }
    }
    $expectedSet = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @($Expected)) { [void]$expectedSet.Add($name) }
    if ($actualSet.Count -ne $expectedSet.Count) {
        throw "$Label count mismatch. Expected $($expectedSet.Count); found $($actualSet.Count)."
    }
    foreach ($name in $expectedSet) {
        if (-not $actualSet.Contains($name)) { throw "Missing $Label name: $name" }
    }
    foreach ($name in $actualSet) {
        if (-not $expectedSet.Contains($name)) { throw "Unexpected $Label name: $name" }
    }
}

function Get-ExpectedLauncherNames {
    $source = Join-Path $repo 'scripts\portable-launchers'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Portable launcher source is missing: $source"
    }
    $files = @(Get-ChildItem -LiteralPath $source -Filter '*.cmd' -File -Force | Sort-Object Name)
    if ($files.Count -ne 5) { throw "Expected exactly five source launchers; found $($files.Count)." }
    foreach ($prefix in @('06_', '06A_', '07_', '07A_', '99_')) {
        if (@($files | Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) {
            throw "Expected exactly one launcher beginning with $prefix"
        }
    }
    foreach ($file in $files) { Assert-CmdBytes $file.FullName }
    @($files.Name)
}

function Assert-NoReparsePoints([string]$Root) {
    $items = @((Get-Item -LiteralPath $Root -Force)) + @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release contains a reparse point: $($item.FullName)"
        }
    }
}

function Assert-TextPrivacyAndSecrets([string]$Text, [string]$Path, [switch]$IncludeCredentialAssignments) {
    if ($Text -match '(?<![A-Za-z])[A-Za-z]:[\\/]') {
        throw "Absolute Windows path found in release text: $Path"
    }
    if ($Text -match '(?i)(github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16}|AIza[0-9A-Za-z_-]{20,}|xox[baprs]-[0-9A-Za-z-]{10,}|BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY)' -or
        ($IncludeCredentialAssignments -and $Text -match '(?i)\b(api[_-]?key|client[_-]?secret|access[_-]?token|password)\b\s*[:=]\s*\S{8,}')) {
        throw "Secret-like value found in release text: $Path"
    }
}

function Read-StrictUtf8NonEmpty([string]$Path, [string]$Label) {
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
    Assert-TextPrivacyAndSecrets $text $Path -IncludeCredentialAssignments
    $text
}

function Assert-OperationManual([string]$Root) {
    $rootManual = Join-Path $Root $manualFileName
    $runtimeManual = Join-Path $Root $manualRuntimeRelativePath
    $rootText = Read-StrictUtf8NonEmpty $rootManual 'Release-root operation manual'
    $runtimeText = Read-StrictUtf8NonEmpty $runtimeManual 'Runtime operation manual'
    $canonicalManual = Join-Path $repo $manualFileName
    $canonicalText = Read-StrictUtf8NonEmpty $canonicalManual 'Canonical repository operation manual'
    foreach ($manualText in @($canonicalText, $rootText, $runtimeText)) {
        if ($manualText -match '(?i)(?:/Users/|/home/|\.codex(?:[/\\]|\b)|OwnerOnly)') {
            throw 'Release operation manual contains private or internal OwnerOnly information.'
        }
    }
    $rootHash = (Get-FileHash -LiteralPath $rootManual -Algorithm SHA256).Hash.ToUpperInvariant()
    $runtimeHash = (Get-FileHash -LiteralPath $runtimeManual -Algorithm SHA256).Hash.ToUpperInvariant()
    $canonicalHash = (Get-FileHash -LiteralPath $canonicalManual -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($rootHash -ne $runtimeHash -or $rootHash -ne $canonicalHash) {
        throw "Repository and release operation manuals are not byte-identical. Repository=$canonicalHash Root=$rootHash Runtime=$runtimeHash"
    }
    $rootHash
}

function Assert-NoSensitiveText([string]$Root) {
    $privateRoot = [IO.Path]::GetFullPath((Join-Path $repo '..\..\..')).TrimEnd('\')
    $extensions = @('.md','.txt','.json','.xml','.config','.cmd','.ps1','.yml','.yaml')
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Force -Recurse) {
        if ($file.Extension -notin $extensions) { continue }
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match '(?<![A-Za-z])[A-Za-z]:[\\/]') {
            throw "Absolute Windows path found in release text: $($file.FullName)"
        }
        if ($text.IndexOf($privateRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE) -and
             $text.IndexOf($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            throw "Private workspace path found in release text: $($file.FullName)"
        }
        Assert-TextPrivacyAndSecrets $text $file.FullName
    }
}

function Assert-BinaryManifest([string]$Root) {
    $manifest = Join-Path $Root 'BINARY_SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'BINARY_SHA256SUMS.txt missing.' }
    $releaseManifest = Join-Path $Root 'RELEASE_BINARY_MANIFEST.sha256'
    if (-not (Test-Path -LiteralPath $releaseManifest -PathType Leaf)) { throw 'RELEASE_BINARY_MANIFEST.sha256 missing.' }
    if ([IO.File]::ReadAllText($manifest) -ne [IO.File]::ReadAllText($releaseManifest)) {
        throw 'Release binary manifests differ.'
    }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $count = 0
    foreach ($line in [IO.File]::ReadAllLines($manifest,[Text.Encoding]::UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw "Invalid binary manifest line: $line" }
        $relative = $matches[2].Replace('\','/')
        Assert-SafeEntry $relative
        if (-not $relative.StartsWith('Program/App/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Binary manifest path is outside Program/App: $relative"
        }
        if (-not $seen.Add($relative)) { throw "Duplicate binary manifest path: $relative" }
        $path = [IO.Path]::GetFullPath((Join-Path $Root $relative.Replace('/','\')))
        if (-not $path.StartsWith(([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Binary manifest path escaped the release root: $relative"
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest file missing: $path" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $matches[1].ToUpperInvariant()) { throw "Binary hash mismatch: $path" }
        $count++
    }
    $actual = @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\App') -File -Recurse)
    if ($count -ne $actual.Count) { throw "Binary manifest coverage mismatch: $count != $($actual.Count)" }
    foreach ($file in $actual) {
        $relative = $file.FullName.Substring($Root.TrimEnd('\').Length + 1).Replace('\','/')
        if (-not $seen.Contains($relative)) { throw "Binary manifest omitted: $relative" }
    }
    $count
}

function Assert-ReleaseTree([string]$Root,[string]$ExpectedFlavor) {
    $Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Release directory missing: $Root" }
    Assert-NoReparsePoints $Root

    $launcherNames = @(Get-ExpectedLauncherNames)
    $requiredRootFiles = @(
        'README.md','INSTALL.md','USER_GUIDE.md','BUILDING.md','TROUBLESHOOTING.md',
        'SECURITY.md','PRIVACY.md','CHANGELOG.md','CONTRIBUTING.md','LICENSE_STATUS.md',
        $manualFileName,'START_HERE.txt','LICENSE','LICENSE_STATUS.txt','THIRD_PARTY_NOTICES.txt','DOTNET_LICENSE.txt',
        'DOTNET_THIRD_PARTY_NOTICES.txt','BINARY_SHA256SUMS.txt','RELEASE_BINARY_MANIFEST.sha256',
        'BUILD_INFO.txt','PORTABLE_RELEASE.txt'
    ) + $launcherNames
    $rootFiles = @(Get-ChildItem -LiteralPath $Root -File -Force)
    $statusPath = Join-Path $Root 'LICENSE_STATUS.txt'
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw 'LICENSE_STATUS.txt missing.' }
    $statusLines = @([IO.File]::ReadAllLines($statusPath, [Text.Encoding]::UTF8))
    $expectedStatusLines = @('LICENSE_INCLUDED','SPDX_IDENTIFIER=MIT','Copyright (c) 2026 ru')
    if ($statusLines.Count -ne $expectedStatusLines.Count) {
        throw "LICENSE_STATUS.txt line count mismatch: $($statusLines.Count)"
    }
    for ($index = 0; $index -lt $expectedStatusLines.Count; $index++) {
        if ($statusLines[$index] -cne $expectedStatusLines[$index]) {
            throw "LICENSE_STATUS.txt line $($index + 1) mismatch: $($statusLines[$index])"
        }
    }
    Assert-ExactNameSet @($rootFiles.Name) $requiredRootFiles 'release root file'
    Assert-CanonicalLicense (Join-Path $Root 'LICENSE') 'Canonical release LICENSE' | Out-Null

    Assert-ExactNameSet @(Get-ChildItem -LiteralPath $Root -Directory -Force | ForEach-Object Name) @('Program','Recordings','docs') 'release root directory'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program') -File -Force | ForEach-Object Name) @('project-root.marker') 'Program file'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program') -Directory -Force | ForEach-Object Name) @('App','Docs','State') 'Program directory'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\App') -File -Force | ForEach-Object Name) @() 'Program/App file'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\App') -Directory -Force | ForEach-Object Name) @('Launcher','Recorder','Player','Watchdog') 'Program/App directory'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\Docs') -File -Force | ForEach-Object Name) @($manualFileName) 'Program/Docs file'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\Docs') -Directory -Force | ForEach-Object Name) @() 'Program/Docs directory'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\State') -File -Force | ForEach-Object Name) @() 'Program/State file'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'Program\State') -Directory -Force | ForEach-Object Name) @('Logs','Settings') 'Program/State directory'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs') -File -Force | ForEach-Object Name) @('FAQ.md','INPUT_MODES.md','SECURITY_MODEL.md') 'docs file'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs') -Directory -Force | ForEach-Object Name) @('images') 'docs directory'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs\images') -File -Force | ForEach-Object Name) $ExpectedImages 'release image'
    Assert-ExactNameSet @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs\images') -Directory -Force | ForEach-Object Name) @() 'release image directory'

    foreach ($relative in @('Program\State\Logs','Program\State\Settings','Recordings')) {
        $path = Join-Path $Root $relative
        if (@(Get-ChildItem -LiteralPath $path -Force).Count -ne 0) { throw "Release directory is not empty: $relative" }
    }
    if ([IO.File]::ReadAllText((Join-Path $Root 'Program\project-root.marker'), [Text.Encoding]::UTF8).Trim() -ne 'MOUSE_KEYBOARD_MACRO_ROOT_V2') {
        throw 'Invalid project-root.marker content.'
    }

    $folders = @{Recorder='MacroRecorder.exe';Player='MacroPlayer.exe';Watchdog='MacroSafetyWatchdog.exe';Launcher='MacroLauncher.exe'}
    foreach ($entry in $folders.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root "Program\App\$($entry.Key)\$($entry.Value)") -PathType Leaf)) { throw "App missing: $($entry.Key)" }
    }
    foreach ($launcher in Get-ChildItem -LiteralPath $Root -Filter '*.cmd' -File -Force) { Assert-CmdBytes $launcher.FullName }
    foreach ($image in $ExpectedImages) { Assert-Png (Join-Path $Root "docs\images\$image") }

    $forbidden = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Where-Object {
        $_.Extension -in @('.macro','.pdb','.log') -or
        $_.Name -in @('.gitkeep','current_session.json','active_tool.json','active_tool.lock','recorder-settings.json')
    })
    if ($forbidden.Count -ne 0) { throw "User/state/debug artifact found in release: $($forbidden[0].FullName)" }

    $portable = [IO.File]::ReadAllText((Join-Path $Root 'PORTABLE_RELEASE.txt'),[Text.Encoding]::UTF8)
    foreach ($contract in @(
        'PORTABLE_RELEASE=TRUE','PRODUCT=MouseKeyboardMacro','RUNTIME_IDENTIFIER=win-x64',
        "FLAVOR=$ExpectedFlavor",'LICENSE_INCLUDED','SPDX_IDENTIFIER=MIT','Copyright (c) 2026 ru','LIVE_INPUT_EXECUTED=NO',
        'REGISTRY_MODIFIED=NO','UAC_REQUESTED=NO'
    )) {
        if ($portable -notmatch ('(?m)^' + [regex]::Escape($contract) + '\r?$')) { throw "Portable contract missing: $contract" }
    }

    $buildInfo = [IO.File]::ReadAllText((Join-Path $Root 'BUILD_INFO.txt'),[Text.Encoding]::UTF8)
    foreach ($contract in @('LICENSE_INCLUDED','SPDX_IDENTIFIER=MIT','Copyright (c) 2026 ru')) {
        if ($buildInfo -notmatch ('(?m)^' + [regex]::Escape($contract) + '\r?$')) { throw "Build information contract missing: $contract" }
    }

    $manualHash = Assert-OperationManual $Root
    Assert-BinaryManifest $Root | Out-Null
    Assert-MarkdownLinks $Root
    Assert-NoSensitiveText $Root
    $manualHash
}

function Invoke-SafeSmoke([string]$Root) {
    $old = $env:MKM_SAFE_VALIDATION_MODE
    $env:MKM_SAFE_VALIDATION_MODE = '1'
    try {
        foreach ($relative in @('Program\App\Recorder\MacroRecorder.exe','Program\App\Player\MacroPlayer.exe','Program\App\Watchdog\MacroSafetyWatchdog.exe')) {
            & (Join-Path $Root $relative) --safe-smoke
            if ($LASTEXITCODE -ne 0) { throw "Safe smoke failed: $relative" }
        }
        & (Join-Path $Root 'Program\App\Launcher\MacroLauncher.exe') --tool recorder --mode medium --project-root $Root --safe-validation
        if ($LASTEXITCODE -ne 0) { throw 'Launcher safe smoke failed.' }
    }
    finally { $env:MKM_SAFE_VALIDATION_MODE = $old }
}

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (-not [string]::IsNullOrWhiteSpace($ReleaseAssets)) {
        $assets = $ReleaseAssets
        if (-not (Test-Path -LiteralPath $assets -PathType Container)) { throw "Release assets directory missing: $assets" }
        $zips = @(
            'MouseKeyboardMacro-v1.0.0-win-x64-framework-dependent.zip',
            'MouseKeyboardMacro-v1.0.0-win-x64-self-contained.zip'
        )
        $assetNames = $zips + @('SHA256SUMS.txt','RELEASE_NOTES_v1.0.0.md')
        Assert-ExactNameSet @(Get-ChildItem -LiteralPath $assets -File -Force | ForEach-Object Name) $assetNames 'release asset file'
        Assert-ExactNameSet @(Get-ChildItem -LiteralPath $assets -Directory -Force | ForEach-Object Name) @() 'release asset directory'
        $notesPath = Join-Path $assets 'RELEASE_NOTES_v1.0.0.md'
        if ([IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8) -notmatch '1\.0\.0') {
            throw 'Release notes do not identify version 1.0.0.'
        }
        Assert-NoSensitiveText $assets

        $sumsPath = Join-Path $assets 'SHA256SUMS.txt'
        $sumLines = @([IO.File]::ReadAllLines($sumsPath,[Text.Encoding]::UTF8) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($sumLines.Count -ne 2) { throw "SHA256SUMS.txt must contain exactly two entries; found $($sumLines.Count)." }
        $expected = @{}
        foreach ($line in $sumLines) {
            if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^/\\]+)$') { throw "Invalid SHA256SUMS.txt line: $line" }
            $name = $matches[2]
            if ($name -notin $zips) { throw "Unexpected SHA256SUMS.txt target: $name" }
            if ($expected.ContainsKey($name)) { throw "Duplicate SHA256SUMS.txt target: $name" }
            $expected[$name] = $matches[1].ToUpperInvariant()
        }
        Assert-ExactNameSet @($expected.Keys) $zips 'SHA256SUMS target'

        $releaseAssetLicenseHash = $null
        foreach ($name in $zips) {
            $zipPath = Join-Path $assets $name
            if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "ZIP missing: $name" }
            if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expected[$name]) { throw "ZIP hash mismatch: $name" }
            $versionRoot = 'MouseKeyboardMacro-v1.0.0'
            $zipLicenseHash = $null
            $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
            try {
                if ($archive.Entries.Count -eq 0) { throw "ZIP is empty: $name" }
                $names = New-Object System.Collections.Generic.List[string]
                $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                foreach ($entry in $archive.Entries) {
                    $entryName = $entry.FullName
                    Assert-SafeEntry $entryName
                    if (-not $seen.Add($entryName)) { throw "Duplicate ZIP entry: $entryName" }
                    if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                        throw "Symbolic-link ZIP entry is forbidden: $entryName"
                    }
                    $names.Add($entryName)
                }
                $licenseEntries = @($archive.Entries | Where-Object { $_.FullName -ceq "$versionRoot/LICENSE" })
                if ($licenseEntries.Count -ne 1) {
                    throw "ZIP must contain exactly one canonical LICENSE entry; found $($licenseEntries.Count) in $name"
                }
                $licenseStream = $licenseEntries[0].Open()
                try {
                    $zipLicenseHash = Get-StreamSha256 $licenseStream
                }
                finally {
                    $licenseStream.Dispose()
                }
                if ($zipLicenseHash -ne $CanonicalLicenseSha256) {
                    throw "Canonical LICENSE hash mismatch in ${name}: $zipLicenseHash"
                }
                if ($null -eq $releaseAssetLicenseHash) {
                    $releaseAssetLicenseHash = $zipLicenseHash
                }
                elseif ($zipLicenseHash -ne $releaseAssetLicenseHash) {
                    throw "Release ZIP LICENSE hashes differ: $name"
                }
            }
            finally { $archive.Dispose() }

            foreach ($entryName in $names) {
                if ($entryName -ne "$versionRoot/" -and
                    -not $entryName.StartsWith("$versionRoot/", [StringComparison]::Ordinal)) {
                    throw "Version root mismatch in ${name}: $entryName"
                }
            }
            foreach ($emptyDirectory in @('Program/State/Logs/','Program/State/Settings/','Recordings/')) {
                if (-not $seen.Contains("$versionRoot/$emptyDirectory")) {
                    throw "ZIP does not preserve empty directory: $emptyDirectory in $name"
                }
            }

            $destination = Join-Path $tempRoot ([IO.Path]::GetFileNameWithoutExtension($name))
            [IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$destination)
            $flavor = if ($name -like '*self-contained*') { 'self-contained' } else { 'framework-dependent' }
            $tree = Join-Path $destination $versionRoot
            $manualHash = Assert-ReleaseTree $tree $flavor
            if ($SafeSmoke) { Invoke-SafeSmoke $tree }
            $report.Add("ZIP=$name|SHA256=$($expected[$name])|LICENSE_SHA256=$zipLicenseHash|MANUAL_SHA256=$manualHash|MANUAL_PARITY=PASS|FLAVOR=$flavor|RESULT=PASS")
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $ReleaseRoot -PathType Container)) { throw "Release root missing: $ReleaseRoot" }
        $directories = @(
            if (Test-Path -LiteralPath (Join-Path $ReleaseRoot 'PORTABLE_RELEASE.txt') -PathType Leaf) {
                Get-Item -LiteralPath $ReleaseRoot -Force
            }
            else {
                Get-ChildItem -LiteralPath $ReleaseRoot -Directory -Force -ErrorAction Stop
            }
        )
        if ($directories.Count -eq 0) { throw "No release directories found under: $ReleaseRoot" }
        foreach ($directory in $directories) {
            $portablePath = Join-Path $directory.FullName 'PORTABLE_RELEASE.txt'
            if (-not (Test-Path -LiteralPath $portablePath -PathType Leaf)) { throw "Not a release directory: $($directory.FullName)" }
            $portableText = [IO.File]::ReadAllText($portablePath, [Text.Encoding]::UTF8)
            if ($portableText -notmatch '(?m)^FLAVOR=(framework-dependent|self-contained)\r?$') {
                throw "Release flavor is missing or invalid: $($directory.FullName)"
            }
            $flavor = $matches[1]
            if (-not [string]::IsNullOrWhiteSpace($ExpectedFlavor) -and $flavor -ne $ExpectedFlavor) {
                throw "Expected flavor $ExpectedFlavor but found $flavor in $($directory.FullName)"
            }
            $manualHash = Assert-ReleaseTree $directory.FullName $flavor
            if ($SafeSmoke) { Invoke-SafeSmoke $directory.FullName }
            $report.Add("DIRECTORY=$($directory.Name)|MANUAL_SHA256=$manualHash|MANUAL_PARITY=PASS|FLAVOR=$flavor|RESULT=PASS")
        }
    }
    $report.Insert(0,'SCHEMA=verify-release-v4')
    $report.Add("EXECUTION=$(if($SafeSmoke){'SAFE_SMOKE'}else{'NO_EXECUTE'})")
    $report.Add('RESULT=PASS')
}
finally {
    $tempFull = [IO.Path]::GetFullPath($tempRoot).TrimEnd('\')
    $tempPrefix = [IO.Path]::GetFullPath($tempParent).TrimEnd('\') + '\'
    if (-not $tempFull.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe verification cleanup path: $tempFull"
    }
    if (Test-Path -LiteralPath $tempFull) {
        Assert-NoReparsePoints $tempFull
        Remove-Item -LiteralPath $tempFull -Force -Recurse
    }
    if (-not $tempParentExisted -and (Test-Path -LiteralPath $tempParent -PathType Container) -and
        @(Get-ChildItem -LiteralPath $tempParent -Force).Count -eq 0) {
        $parentItem = Get-Item -LiteralPath $tempParent -Force
        if (($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Temporary parent became a reparse point: $tempParent" }
        Remove-Item -LiteralPath $tempParent -Force
    }
}
$reportDirectory = Split-Path -Parent $ReportPath
if ($reportDirectory) { New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null }
[IO.File]::WriteAllText($ReportPath,(($report -join "`n")+"`n"),(New-Object Text.UTF8Encoding($false)))
$report
