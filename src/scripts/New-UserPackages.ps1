#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory
)

# Three end-user ZIPs. Build provenance stays in artifacts, outside the delivery directory.
# Reuse the full packager's build/source/game/dependency/asset checks rather than bypassing them.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$srcRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$comparison = [StringComparison]::OrdinalIgnoreCase
$utf8 = [Text.UTF8Encoding]::new($false)

function Is-Within([string]$Path,[string]$Root) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $Path.Equals($base,$comparison) -or $Path.StartsWith($base + '\',$comparison)
}
function Assert-NoReparse([string]$Path) {
    $ancestor = $Path
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse-point path is not accepted: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}
function Assert-EmptyOutput {
    Assert-NoReparse $outputRoot
    if (Test-Path -LiteralPath $outputRoot) {
        if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
            @(Get-ChildItem -LiteralPath $outputRoot -Force).Count) {
            throw 'OutputDirectory must be nonexistent or empty; existing packages are never replaced.'
        }
    }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function New-Json([string]$Path,$Value) {
    $bytes = $utf8.GetBytes(($Value | ConvertTo-Json -Depth 20))
    $stream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}

Assert-EmptyOutput
if ((Is-Within $outputRoot $srcRoot) -and -not (Is-Within $outputRoot (Join-Path $srcRoot 'artifacts'))) {
    throw 'OutputDirectory inside src must be under artifacts.'
}
$build = Get-Content -LiteralPath (Join-Path $srcRoot 'artifacts/source-handover-build-Release.json') -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($build.GameRoot) -or (Is-Within $outputRoot $build.GameRoot)) {
    throw 'A full build is required; the delivery directory cannot be inside the game installation.'
}
$auditRoot = Join-Path $srcRoot ('artifacts/user-packages/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$stage = Join-Path $auditRoot 'validated-stage'
& (Join-Path $PSScriptRoot 'New-ModPackages.ps1') -Configuration Release -OutputDirectory $stage
$receiptPath = Join-Path $stage 'package-receipt.json'
$receiptHash = Hash $receiptPath
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json

$definitions = @(
    @{Name='RukariLib'; Guide='rukari-lib.txt'; Dlls=@('Rukari.Lib.dll','Rukari.Lib.Runtime.dll')},
    @{Name='更多的画面效果'; Guide='more-effects.txt'; Dlls=@('Rukari.MoreEffects.dll','AzureArchive.VideoTools.Core.dll','AzureArchive.VideoTools.Formats.dll')},
    @{Name='人物配音支持'; Guide='character-voice.txt'; Dlls=@('Rukari.CharacterVoice.dll')}
)
$archives = [Collections.Generic.List[object]]::new()
foreach ($definition in $definitions) {
    $matches = @($receipt.Packages | Where-Object Name -CEQ $definition.Name)
    if ($matches.Count -ne 1) { throw "Missing validated package: $($definition.Name)" }
    $package = $matches[0]
    $prefix = $package.Folder + '/'
    $files = @($receipt.Files | Where-Object { $_.Path.StartsWith($prefix,[StringComparison]::Ordinal) })
    $dlls = @($files | Where-Object { $_.Path.EndsWith('.dll',[StringComparison]::Ordinal) } | ForEach-Object { [IO.Path]::GetFileName($_.Path) })
    if ($dlls.Count -ne $definition.Dlls.Count -or @(Compare-Object $dlls $definition.Dlls).Count) {
        throw "Unexpected runtime DLL set: $($definition.Name)"
    }
    if ($definition.Name -cne 'RukariLib' -and @($files | Where-Object { $_.Path.Contains('/ui/') }).Count) {
        throw 'Feature packages cannot duplicate the shared UI assets.'
    }
    $guide = Join-Path $srcRoot ('packaging/user/' + $definition.Guide)
    Assert-NoReparse $guide
    if (-not (Test-Path -LiteralPath $guide -PathType Leaf)) { throw "Missing user guide: $guide" }
    $inputs = @($files | ForEach-Object {
        [pscustomobject]@{Path=(Join-Path $stage $_.Path);Entry=$_.Path;SHA256=$_.SHA256}
    })
    $inputs += [pscustomobject]@{Path=$guide;Entry='安装说明.txt';SHA256=(Hash $guide)}
    $zipName = $definition.Name + '-' + $package.Version + '-AA1.0-test-' + (Get-Date -Format 'yyyyMMdd') + '.zip'
    $zipPath = Join-Path $auditRoot $zipName
    $stream = [IO.File]::Open($zipPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            foreach ($inputFile in $inputs) {
                if ((Hash $inputFile.Path) -cne $inputFile.SHA256) { throw "Staging input changed: $($inputFile.Entry)" }
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$inputFile.Path,$inputFile.Entry,[IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }

    # Read every compressed entry back, reject duplicates/extras, and compare decompressed bytes.
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne $inputs.Count) { throw "Unexpected ZIP inventory: $zipName" }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $zip.Entries) {
            if (-not $seen.Add($entry.FullName)) { throw "Duplicate ZIP entry: $($entry.FullName)" }
            $expected = @($inputs | Where-Object Entry -CEQ $entry.FullName)
            if ($expected.Count -ne 1) { throw "Unplanned ZIP entry: $($entry.FullName)" }
            $entryStream = $entry.Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($hasher.ComputeHash($entryStream)) }
            finally { $hasher.Dispose(); $entryStream.Dispose() }
            if ($actual -cne $expected[0].SHA256) { throw "ZIP entry hash mismatch: $($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
    $archives.Add([pscustomobject]@{Name=$package.Name;Version=$package.Version;File=$zipName;SHA256=(Hash $zipPath);Length=(Get-Item -LiteralPath $zipPath).Length;Entries=$inputs.Count;Inputs=$inputs})
}

# Publish only the three completed, verified ZIPs. Receipts and four-package build staging stay outside.
Assert-EmptyOutput
if ((Hash $receiptPath) -cne $receiptHash) { throw 'Validated package receipt changed during packaging.' }
[void][IO.Directory]::CreateDirectory($outputRoot)
foreach ($archive in $archives) {
    $sourceZip = Join-Path $auditRoot $archive.File
    $targetZip = Join-Path $outputRoot $archive.File
    [IO.File]::Copy($sourceZip,$targetZip,$false)
    if ((Hash $targetZip) -cne $archive.SHA256) { throw "Delivery hash mismatch: $($archive.File)" }
}
New-Json (Join-Path $auditRoot 'user-package-receipt.json') ([ordered]@{
    SchemaVersion=1;PackagedAt=(Get-Date -Format o);OutputDirectory=$outputRoot;PackageReceiptSHA256=$receiptHash
    Packages=@($archives.ToArray());Target='AA 1.0';Status='test';Compilation=$receipt.Compilation
    ManagedTests=$receipt.ManagedTests;Native='not-run';GameStarted=$false;Installed=$false;GitHubReleasePublished=$false
})
$archives | Select-Object Name,Version,File,Length,Entries,SHA256 | Format-Table -AutoSize
Write-Output "User packages: $outputRoot"
Write-Output "Private build/ZIP validation receipt: $(Join-Path $auditRoot 'user-package-receipt.json')"
