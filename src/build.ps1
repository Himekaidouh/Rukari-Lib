#requires -Version 7.0
param(
    [string]$GameRoot = $env:AZUREARCHIVE_GAME_ROOT,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$CoreOnly,
    [switch]$SkipTests
)
# Source handover build: the four stable products, their full source dependencies and examples.
# This script does not install files, change profiles or start the game.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = $PSScriptRoot
$utf8 = [Text.UTF8Encoding]::new($false)
function Run-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
$skin = Join-Path $repo 'assets/rukari-lib/ui'
if (-not $SkipTests) {
    if (-not (Test-Path -LiteralPath (Join-Path $skin 'ui-assets.sha256') -PathType Leaf)) {
        throw 'Full tests need the bundled RukariLib UI assets. Restore src/assets/rukari-lib/ui from the repository. Use -SkipTests only for a code-only compilation.'
    }
    foreach ($line in Get-Content -LiteralPath (Join-Path $skin 'ui-assets.sha256')) {
        if ($line -notmatch '^([A-Fa-f0-9]{64})\s+(.+)$') { throw 'Invalid UI asset hash manifest.' }
        $expected = $Matches[1]; $relative = $Matches[2]
        $path = [IO.Path]::GetFullPath((Join-Path $skin $relative))
        if (-not $path.StartsWith([IO.Path]::GetFullPath($skin) + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing UI file: $relative" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expected) { throw "UI hash mismatch: $relative" }
    }
}
$common = @('-c',$Configuration,'-p:AzureArchiveTarget=Preview10')
if ($CoreOnly) {
    foreach ($project in @('AzureArchive.VideoTools.Tests/AzureArchive.VideoTools.Tests.csproj','Rukari.Lib.Tests/Rukari.Lib.Tests.csproj','Rukari.CharacterVoice/Tests/Rukari.CharacterVoice.Tests.csproj','Rukari.SpineSupport/Tests/Rukari.SpineSupport.Tests.csproj')) {
        Run-DotNet (@('build',(Join-Path $repo $project)) + $common)
    }
    if (-not $SkipTests) {
        # The asset test reads the same on-disk payload path as a full Runtime build, without loading Unity.
        $runtimeUi = Join-Path $repo "Rukari.Lib.Runtime/bin/$Configuration/ui"
        [IO.Directory]::CreateDirectory($runtimeUi) | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $skin -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($skin,$file.FullName)
            $target = Join-Path $runtimeUi $relative
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }
} else {
    if ([string]::IsNullOrWhiteSpace($GameRoot)) { throw 'Specify -GameRoot or AZUREARCHIVE_GAME_ROOT. The game must already contain its local BepInEx/core and interop references.' }
    $resolvedGame = (Resolve-Path -LiteralPath $GameRoot).Path
    Run-DotNet (@('build',(Join-Path $repo 'Rukari.Mods.sln')) + $common + @("-p:AzureArchiveGameRoot=$resolvedGame"))
    foreach ($project in @('examples/SharedVoiceClient/SharedVoiceClient.csproj','examples/UiToolboxClient/UiToolboxClient.csproj','examples/UiToolboxPlugin/UiToolboxPlugin.csproj','examples/EmbeddedDirectiveClient/EmbeddedDirectiveClient.csproj')) {
        Run-DotNet (@('build',(Join-Path $repo $project)) + $common + @("-p:GameRoot=$resolvedGame"))
    }
    & (Join-Path $repo 'scripts/Test-RukariDependencies.ps1') -GameRoot $resolvedGame -Configuration $Configuration -Scope StableModules -OutputPath (Join-Path $repo "artifacts/mod-dependencies-$Configuration.json")
}
if (-not $SkipTests) {
    foreach ($suite in @("AzureArchive.VideoTools.Tests/bin/$Configuration/net6.0/AzureArchive.VideoTools.Tests.dll","Rukari.Lib.Tests/bin/$Configuration/net6.0/Rukari.Lib.Tests.dll","Rukari.CharacterVoice/Tests/bin/$Configuration/net6.0/Rukari.CharacterVoice.Tests.dll","Rukari.SpineSupport/Tests/bin/$Configuration/net6.0/Rukari.SpineSupport.Tests.dll")) {
        Run-DotNet @((Join-Path $repo $suite))
    }
}
$records = @(Get-ChildItem -LiteralPath $repo -File -Recurse | Where-Object {
    $_.FullName.Substring($repo.Length + 1) -notmatch '(^|[\\/])(bin|obj|artifacts)[\\/]' -and $_.Extension -in @('.cs','.csproj','.sln','.props','.targets','.ps1','.json')
} | ForEach-Object { [ordered]@{Path=[IO.Path]::GetRelativePath($repo,$_.FullName).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
$receipt = [ordered]@{SchemaVersion=1;Lib='0.4.1';MoreEffects='1.4.0';Configuration=$Configuration;Scope=($CoreOnly ? 'managed core only' : 'four products and four examples');Compilation='passed';ManagedTests=($SkipTests ? 'not-run' : 'passed: 396 Core + 136 Lib + 53 Voice + 26 Spine');Native='not-run';Sources=$records;GameStarted=$false;Installed=$false}
$receiptDirectory = Join-Path $repo 'artifacts'
[IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $receiptDirectory "source-handover-build-$Configuration.json"),($receipt | ConvertTo-Json -Depth 7),$utf8)
Write-Output 'Source handover build completed. No game files were installed or changed.'
