#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [switch]$Zip
)

# Stage only the four supported packages after a complete Release10 build.
# Never install, launch the game, alter a profile, delete files, or overwrite output.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$srcRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repositoryRoot = Split-Path -Parent $srcRoot
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$comparison = [StringComparison]::OrdinalIgnoreCase

function Is-Within([string]$Path, [string]$Root) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $Path.Equals($base, $comparison) -or $Path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, $comparison)
}

function Resolve-Child([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) {
        throw "Expected a nonempty relative path: $Relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not (Is-Within $path $Root) -or $path.Equals([IO.Path]::GetFullPath($Root), $comparison)) {
        throw "Path escapes its expected root: $Relative"
    }
    $path
}

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Reparse-point input is not accepted: $Path"
    }
}

function Get-Hash([string]$Path) {
    Assert-File $Path
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-Hash $Path) -ine $Expected) {
        throw "Hash does not match its receipt: $Path"
    }
}

function Write-NewText([string]$Path, [string]$Content) {
    $bytes = $utf8.GetBytes($Content)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Assert-EmptyOutput {
    if (Test-Path -LiteralPath $outputRoot) {
        if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
            @(Get-ChildItem -LiteralPath $outputRoot -Force).Count -ne 0) {
            throw 'OutputDirectory must be nonexistent or empty. Existing output is never replaced.'
        }
    }
    # Do not write through a junction/symlink hidden in an existing output ancestor.
    $ancestor = $outputRoot
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Output ancestor is a reparse point: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}

Assert-EmptyOutput
if ((Is-Within $outputRoot $srcRoot) -and -not (Is-Within $outputRoot (Join-Path $srcRoot 'artifacts'))) {
    throw 'A staging directory inside src must be under src/artifacts, not among production sources.'
}

$buildPath = Join-Path $srcRoot "artifacts/source-handover-build-$Configuration.json"
$dependencyPath = Join-Path $srcRoot "artifacts/mod-dependencies-$Configuration.json"
Assert-File $buildPath
Assert-File $dependencyPath
$buildHash = Get-Hash $buildPath
$dependencyHash = Get-Hash $dependencyPath
$build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
$dependencies = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json
if ($build.SchemaVersion -ne 2 -or $build.Target -cne 'Release10' -or
    $build.Configuration -cne $Configuration -or $build.Compilation -cne 'passed' -or
    $build.Scope -cne 'four products and four examples' -or
    $build.ManagedTests -cnotmatch '^passed:' -or [string]::IsNullOrWhiteSpace($build.GameRoot)) {
    throw 'A successful SchemaVersion 2 Release10 full build with managed tests is required. Run src/build.ps1 without CoreOnly or SkipTests.'
}
$gameRoot = [IO.Path]::GetFullPath($build.GameRoot)
if (Is-Within $outputRoot $gameRoot) { throw 'OutputDirectory cannot be inside the game installation; this script only stages packages.' }
if (@($build.GameInputs).Count -eq 0) { throw 'The build receipt has no game input hashes.' }
foreach ($inputRecord in $build.GameInputs) {
    Assert-Hash (Resolve-Child $gameRoot $inputRecord.Path) $inputRecord.SHA256
}

# Match the build script's complete source inventory as well as every recorded hash.
# This rejects edited, deleted, or newly added build inputs, including this script.
$recordedSources = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($source in $build.Sources) {
    $relative = ([string]$source.Path).Replace('\','/')
    if ($recordedSources.ContainsKey($relative)) { throw "Duplicate source in build receipt: $relative" }
    Assert-Hash (Resolve-Child $srcRoot $relative) $source.SHA256
    $recordedSources.Add($relative, [string]$source.SHA256)
}
$currentSources = @(Get-ChildItem -LiteralPath $srcRoot -File -Recurse | Where-Object {
    $_.FullName.Substring($srcRoot.Length + 1) -notmatch '(^|[\\/])(bin|obj|artifacts)[\\/]' -and
    $_.Extension -in @('.cs','.csproj','.sln','.props','.targets','.ps1','.json')
} | ForEach-Object { [IO.Path]::GetRelativePath($srcRoot,$_.FullName).Replace('\','/') })
if ($recordedSources.Count -eq 0 -or $recordedSources.Count -ne $currentSources.Count -or
    @($currentSources | Where-Object { -not $recordedSources.ContainsKey($_) }).Count -ne 0) {
    throw 'Current source inventory differs from the successful build receipt. Rebuild before packaging.'
}

if (@($dependencies.Checks).Count -ne 4 -or @($dependencies.Failures).Count -ne 0) {
    throw 'A successful dependency receipt containing exactly four plugins and no failures is required.'
}
Assert-Hash (Join-Path $gameRoot 'BepInEx/core/BepInEx.Core.dll') $dependencies.LoaderCoreSHA256

$definitions = @(
    @{ Project='Rukari.Lib.Runtime'; Name='RukariLib'; Guid='rukari.lib.runtime'; Icon='rukari-lib'; Extra=@('Rukari.Lib') },
    @{ Project='Rukari.CharacterVoice'; Name='人物配音支持'; Guid='rukari.charactervoice'; Icon='character-voice'; Extra=@() },
    @{ Project='Rukari.MoreEffects'; Name='更多的画面效果'; Guid='rukari.moreeffects'; Icon='more-effects'; Extra=@('AzureArchive.VideoTools.Core','AzureArchive.VideoTools.Formats') },
    @{ Project='Rukari.SpineSupport'; Name='更多Spine动画支持'; Guid='rukari.spinesupport'; Icon='memory-lobby'; Extra=@() }
)
$expectedOutputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($definition in $definitions) {
    [void]$expectedOutputs.Add($definition.Project + "/bin/$Configuration/" + $definition.Project + '.dll')
    foreach ($extra in $definition.Extra) {
        [void]$expectedOutputs.Add($extra + "/bin/$Configuration/net6.0/" + $extra + '.dll')
    }
}
if ($build.PSObject.Properties.Name -notcontains 'Outputs' -or @($build.Outputs).Count -ne $expectedOutputs.Count) {
    throw 'The build receipt must record all seven runtime DLL outputs. Rebuild before packaging.'
}
$recordedOutputs = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($outputRecord in $build.Outputs) {
    $relative = ([string]$outputRecord.Path).Replace('\','/')
    if (-not $expectedOutputs.Contains($relative) -or $recordedOutputs.ContainsKey($relative)) {
        throw "Unexpected or duplicate DLL output in build receipt: $relative"
    }
    Assert-Hash (Resolve-Child $srcRoot $relative) $outputRecord.SHA256
    $recordedOutputs.Add($relative, [string]$outputRecord.SHA256)
}
$plans = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
function Add-Payload([string]$Source, [string]$Destination) {
    if ([IO.Path]::GetExtension($Source) -ieq '.dll') {
        $relative = [IO.Path]::GetRelativePath($srcRoot,$Source).Replace('\','/')
        if (-not $recordedOutputs.ContainsKey($relative)) { throw "DLL is absent from the build receipt: $relative" }
        Assert-Hash $Source $recordedOutputs[$relative]
    }
    $destinationPath = Resolve-Child $outputRoot $Destination
    if (@($plans | Where-Object { $_.Destination.Equals($destinationPath,$comparison) }).Count) {
        throw "Duplicate package destination: $Destination"
    }
    $plans.Add([pscustomobject]@{Source=$Source;Destination=$destinationPath;Relative=$Destination.Replace('\','/');SHA256=(Get-Hash $Source)})
}

$libVersion = $null
foreach ($definition in $definitions) {
    $manifestPath = Join-Path $srcRoot ($definition.Project + '/package/manifest.json')
    Assert-File $manifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.name -cne $definition.Name -or [string]$manifest.version_number -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') {
        throw "Unexpected package identity: $manifestPath"
    }
    $checks = @($dependencies.Checks | Where-Object Name -CEQ $definition.Name)
    if ($checks.Count -ne 1 -or $checks[0].PluginGuid -cne $definition.Guid -or
        $checks[0].PluginVersion -cne $manifest.version_number -or $checks[0].Satisfied -ne $true) {
        throw "Manifest/plugin/dependency receipt mismatch: $($definition.Name)"
    }
    $plugin = Join-Path $srcRoot ($definition.Project + "/bin/$Configuration/" + $definition.Project + '.dll')
    if (-not ([IO.Path]::GetFullPath($checks[0].DLL)).Equals($plugin,$comparison)) {
        throw "Dependency receipt references a different plugin output: $($definition.Name)"
    }
    Assert-Hash $plugin $checks[0].SHA256
    if ($definition.Name -ceq 'RukariLib') {
        $libVersion = [string]$manifest.version_number
        if (@($manifest.dependencies).Count -ne 0) { throw 'The shared library manifest must not depend on feature packages.' }
    } elseif (@($manifest.dependencies).Count -ne 1 -or $manifest.dependencies[0] -cne "RukariLib/$libVersion" -or $checks[0].SelectedLib -cne $libVersion) {
        throw "Feature package does not require the selected shared library: $($definition.Name)"
    }
    $folder = 'mods/' + $manifest.name + '/' + $manifest.version_number
    Add-Payload $plugin ($folder + '/' + $definition.Project + '.dll')
    foreach ($extra in $definition.Extra) {
        Add-Payload (Join-Path $srcRoot ($extra + "/bin/$Configuration/net6.0/" + $extra + '.dll')) ($folder + '/' + $extra + '.dll')
    }
    Add-Payload $manifestPath ($folder + '/manifest.json')
    Add-Payload (Join-Path $srcRoot ('assets/' + $definition.Icon + '/icon.png')) ($folder + '/icon.png')
    Add-Payload (Join-Path $repositoryRoot 'LICENSE') ($folder + '/LICENSE')
    Add-Payload (Join-Path $srcRoot 'assets/NOTICE.md') ($folder + '/ART_NOTICE.md')
    $packages.Add([ordered]@{Name=$manifest.name;Version=$manifest.version_number;PluginGuid=$definition.Guid;Folder=$folder})
}

$skin = Join-Path $srcRoot 'assets/rukari-lib/ui'
$skinHashes = Join-Path $skin 'ui-assets.sha256'
Assert-File $skinHashes
$allowedUi = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $skinHashes) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})\s+(.+)$') { throw 'Invalid shared UI hash manifest.' }
    $expected = $Matches[1]
    $relative = $Matches[2].Replace('\','/')
    if (-not $allowedUi.Add($relative)) { throw "Duplicate shared UI hash entry: $relative" }
    Assert-Hash (Resolve-Child $skin $relative) $expected
}
[void]$allowedUi.Add('ui-assets.sha256')
[void]$allowedUi.Add('NOTICE.md')
$skinFiles = @(Get-ChildItem -LiteralPath $skin -File -Recurse -Force)
if ($skinFiles.Count -ne $allowedUi.Count) { throw 'Shared UI file inventory differs from its hash manifest plus notices.' }
foreach ($file in $skinFiles) {
    $relative = [IO.Path]::GetRelativePath($skin,$file.FullName).Replace('\','/')
    if (-not $allowedUi.Contains($relative)) { throw "Unverified shared UI file: $relative" }
    Add-Payload $file.FullName ("mods/RukariLib/$libVersion/ui/" + $relative)
}

# Validation is complete. Create only a new/empty staging tree, with exclusive writes.
Assert-EmptyOutput
Assert-Hash $buildPath $buildHash
Assert-Hash $dependencyPath $dependencyHash
[void][IO.Directory]::CreateDirectory($outputRoot)
foreach ($plan in $plans) {
    Assert-Hash $plan.Source $plan.SHA256
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $plan.Destination))
    [IO.File]::Copy($plan.Source,$plan.Destination,$false)
    Assert-Hash $plan.Destination $plan.SHA256
}
[IO.File]::Copy($buildPath,(Join-Path $outputRoot 'build-receipt.json'),$false)
[IO.File]::Copy($dependencyPath,(Join-Path $outputRoot 'dependency-receipt.json'),$false)
$readme = @'
Rukari 模组：AA 1.0 适配候选

本包含 RukariLib、人物配音支持、更多的画面效果、更多Spine动画支持。
已针对构建记录中的正式 AA 1.0 文件编译并通过托管测试及依赖检查；尚未完成游戏内运行验收（native-not-run）。
mods 内采用“模组名/版本”布局，三个功能模组都需要本包对应版本的 RukariLib。
退出游戏后，将 mods 文件夹合并到 AA 1.0 游戏目录，在官方 Mod 管理中启用 RukariLib 和所需功能模组，再重启游戏。
不含半成品字幕和实验性 Spine 叠加台。此包尚未作为稳定版发布。
代码许可见各包 LICENSE；图片与 UI 素材的权利说明见 ART_NOTICE.md 及共享 UI 的 NOTICE.md。
'@
Write-NewText (Join-Path $outputRoot 'README.txt') $readme
$payload = @($plans | ForEach-Object { [ordered]@{Path=$_.Relative;SHA256=$_.SHA256;Length=(Get-Item -LiteralPath $_.Destination).Length} })
$packageReceipt = [ordered]@{
    SchemaVersion=1;PackagedAt=(Get-Date -Format o);Target='Release10';Configuration=$Configuration
    GameRoot=$gameRoot;BuildReceiptSHA256=$buildHash;DependencyReceiptSHA256=$dependencyHash
    SourcesVerified=$recordedSources.Count;Packages=@($packages.ToArray());Files=$payload
    Compilation='passed';ManagedTests=$build.ManagedTests;DependencyChecks='passed: four plugins'
    Native='not-run';Candidate=$true;Installed=$false;ProfilesChanged=$false;GameStarted=$false
}
Write-NewText (Join-Path $outputRoot 'package-receipt.json') ($packageReceipt | ConvertTo-Json -Depth 12)

if ($Zip) {
    $zipPath = Join-Path $outputRoot "RukariMods-Release10-$Configuration.zip"
    $zipStream = [IO.File]::Open($zipPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($zipStream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            $zipInputs = @($plans | ForEach-Object { [pscustomobject]@{Path=$_.Destination;Entry=$_.Relative} })
            # Keep developer receipts and local machine paths outside the user-facing ZIP.
            foreach ($rootFile in @('README.txt')) {
                $zipInputs += [pscustomobject]@{Path=(Join-Path $outputRoot $rootFile);Entry=$rootFile}
            }
            foreach ($inputFile in $zipInputs) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$inputFile.Path,$inputFile.Entry,[IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $archive.Dispose() }
    } finally { $zipStream.Dispose() }
    $zipReceipt = [ordered]@{File=[IO.Path]::GetFileName($zipPath);SHA256=(Get-Hash $zipPath);Length=(Get-Item -LiteralPath $zipPath).Length;Entries=$zipInputs.Count}
    Write-NewText (Join-Path $outputRoot 'zip-receipt.json') ($zipReceipt | ConvertTo-Json -Depth 4)
}

Write-Output "Staged four candidate packages: $outputRoot"
Write-Output 'No installation, profile change, game launch, overwrite, or deletion was performed. Native validation is not run.'
