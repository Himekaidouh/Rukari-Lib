#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ValidatedPackageDirectory)

# Regression fixtures only: use an existing validated stage, never rebuild, install, or approve a release.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'UserPackageAcceptance.ps1')
$sourceStage = [IO.Path]::GetFullPath($ValidatedPackageDirectory)
$package = Get-Content -LiteralPath (Join-Path $sourceStage 'package-receipt.json') -Raw | ConvertFrom-Json
$buildSource = Join-Path $sourceStage 'build-receipt.json'
$buildHash = (Get-FileHash -LiteralPath $buildSource -Algorithm SHA256).Hash
$definitions = @(
    @{Name='RukariLib';Dlls=@('Rukari.Lib.dll','Rukari.Lib.Runtime.dll')},
    @{Name='更多的画面效果';Dlls=@('Rukari.MoreEffects.dll','AzureArchive.VideoTools.Core.dll','AzureArchive.VideoTools.Formats.dll')},
    @{Name='人物配音支持';Dlls=@('Rukari.CharacterVoice.dll')}
)
$acceptedPackages = @($definitions | ForEach-Object {
    $definition = $_
    $found = @($package.Packages | Where-Object Name -CEQ $definition.Name)
    if ($found.Count -ne 1) { throw 'Fixture needs one validated entry for every user package.' }
    [ordered]@{Name=$definition.Name;Version=$found[0].Version;Dlls=@($definition.Dlls | ForEach-Object {
        $path = $found[0].Folder + '/' + $_
        $file = @($package.Files | Where-Object Path -CEQ $path)
        if ($file.Count -ne 1) { throw "Fixture DLL is absent: $path" }
        [ordered]@{Path=$path;SHA256=$file[0].SHA256}
    })}
})
$goodJson = ([ordered]@{SchemaVersion=1;Kind='user-package-acceptance';Target='AA 1.0 fix6'
    Approved=$true;ConfirmedAt='2026-10-04T00:00:00+08:00';HumanScope='SYNTHETIC REGRESSION FIXTURE ONLY'
    Evidence='No human release acceptance is asserted by this test.';ToolNativeValidation='not-run'
    BuildReceiptSHA256=$buildHash;Packages=$acceptedPackages} | ConvertTo-Json -Depth 15)
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$fixtureRoot = Join-Path $tempBase ('rukari-acceptance-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$results = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Fixture([string]$Name, [string]$Text) {
    $path = Join-Path $fixtureRoot ($Name + '.json')
    [IO.File]::WriteAllText($path,$Text,$utf8)
    $path
}
function Read-Good { $goodJson | ConvertFrom-Json -AsHashtable }
function Assert-Rejected([string]$Name, [scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Reason,[StringComparison]::OrdinalIgnoreCase)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected rejection: $Name" }
    $results.Add($Name)
}
function Assert-Binding([string]$Path) {
    Assert-UserPackageAcceptanceBinding (Read-UserPackageAcceptance $Path) $package $buildSource $definitions
}

try {
    $goodPath = Write-Fixture 'current-bound' $goodJson
    $binding = Assert-Binding $goodPath
    if ($binding.ToolNativeValidation -cne 'not-run' -or $binding.Packages.Count -ne 3) { throw 'Binding result changed native status or inventory.' }
    $results.Add('current structure binds three versions and six DLLs without upgrading Native')
    $legacy = Write-Fixture 'legacy' '用户确认可以了；历史 1.4.x 已通过。'
    Assert-Rejected 'legacy free text cannot approve Stable' { Assert-Binding $legacy } 'legacy free-text'
    $wrong = Read-Good
    $wrong.Packages[1].Version = '0.0.0'
    $wrongPath = Write-Fixture 'wrong-version' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'old package version rejected' { Assert-Binding $wrongPath } 'package/version'
    $wrong = Read-Good
    $wrong.BuildReceiptSHA256 = '0' * 64
    $wrongPath = Write-Fixture 'wrong-build' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'another build receipt rejected' { Assert-Binding $wrongPath } 'build receipt SHA256'
    $wrong = Read-Good
    $wrong.Packages[0].Dlls[0].SHA256 = '0' * 64
    $wrongPath = Write-Fixture 'wrong-dll' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'changed DLL bytes rejected' { Assert-Binding $wrongPath } 'DLL SHA256'
    $wrong = Read-Good
    $wrong.Approved = 'true'
    $wrongPath = Write-Fixture 'string-approval' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'string approval rejected' { Assert-Binding $wrongPath } 'JSON boolean Approved=true'
    $wrong = Read-Good
    $wrong.Approved = $false
    $wrongPath = Write-Fixture 'false-approval' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'false approval rejected' { Assert-Binding $wrongPath } 'JSON boolean Approved=true'
    $wrong = Read-Good
    $wrong.Packages[0].Dlls[1] = $wrong.Packages[0].Dlls[0]
    $wrongPath = Write-Fixture 'duplicate-dll' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'duplicate DLL rejected' { Assert-Binding $wrongPath } 'duplicate Stable acceptance DLL'
    $wrong = Read-Good
    $wrong.Packages[2] = $wrong.Packages[0]
    $wrongPath = Write-Fixture 'duplicate-package' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'duplicate package rejected' { Assert-Binding $wrongPath } 'Duplicate Stable acceptance package'
    $wrong = Read-Good
    $wrong.Packages[0].Dlls = @($wrong.Packages[0].Dlls[0])
    $wrongPath = Write-Fixture 'missing-dll' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'missing DLL rejected' { Assert-Binding $wrongPath } 'every distributable DLL'
    $wrong = Read-Good
    $wrong.ToolNativeValidation = 'passed'
    $wrongPath = Write-Fixture 'native-upgrade' ($wrong | ConvertTo-Json -Depth 15)
    Assert-Rejected 'human record cannot upgrade tool Native' { Assert-Binding $wrongPath } 'never upgrades Native'

    # Exercise the real user ZIP entry point with a fixture-only stage copier.
    # Full production source/game/dependency validation remains New-ModPackages' responsibility.
    $fixtureSrc = Join-Path $fixtureRoot 'src'
    $fixtureScripts = Join-Path $fixtureSrc 'scripts'
    $fixtureInput = Join-Path $fixtureRoot 'validated-input'
    [void][IO.Directory]::CreateDirectory($fixtureScripts)
    [void][IO.Directory]::CreateDirectory($fixtureInput)
    foreach ($scriptName in @('New-UserPackages.ps1','UserPackageAcceptance.ps1','PackagingReadRetry.ps1')) {
        [IO.File]::Copy((Join-Path $PSScriptRoot $scriptName),(Join-Path $fixtureScripts $scriptName),$false)
    }
    foreach ($file in $package.Files) {
        $relative = [string]$file.Path
        $destination = [IO.Path]::GetFullPath((Join-Path $fixtureInput $relative))
        if (-not $destination.StartsWith($fixtureInput + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Fixture package path escapes its temporary root.'
        }
        $source = Join-Path $sourceStage $relative
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $file.SHA256) { throw "Fixture payload hash mismatch: $relative" }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy($source,$destination,$false)
    }
    foreach ($receiptName in @('package-receipt.json','build-receipt.json')) {
        [IO.File]::Copy((Join-Path $sourceStage $receiptName),(Join-Path $fixtureInput $receiptName),$false)
    }
    $fixtureArtifacts = Join-Path $fixtureSrc 'artifacts'
    [void][IO.Directory]::CreateDirectory($fixtureArtifacts)
    [IO.File]::Copy($buildSource,(Join-Path $fixtureArtifacts 'source-handover-build-Release.json'),$false)
    $fixtureGuides = Join-Path $fixtureSrc 'packaging/user'
    [void][IO.Directory]::CreateDirectory($fixtureGuides)
    foreach ($guide in @('rukari-lib.txt','more-effects.txt','character-voice.txt')) {
        [IO.File]::Copy((Join-Path (Split-Path -Parent $PSScriptRoot) ('packaging/user/' + $guide)),(Join-Path $fixtureGuides $guide),$false)
    }
    $stageCopier = @'
param($Configuration,$OutputDirectory)
$ErrorActionPreference='Stop'
$inputRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../validated-input'))
foreach($file in Get-ChildItem -LiteralPath $inputRoot -File -Recurse) {
    $destination=Join-Path $OutputDirectory ([IO.Path]::GetRelativePath($inputRoot,$file.FullName))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    [IO.File]::Copy($file.FullName,$destination,$false)
}
'@
    [IO.File]::WriteAllText((Join-Path $fixtureScripts 'New-ModPackages.ps1'),$stageCopier,$utf8)
    $entry = Join-Path $fixtureScripts 'New-UserPackages.ps1'
    $candidateOutput = Join-Path $fixtureRoot 'candidate-output'
    & $entry -OutputDirectory $candidateOutput -UserAcceptancePath $legacy | Out-Null
    $candidateFiles = @(Get-ChildItem -LiteralPath $candidateOutput -File)
    if ($candidateFiles.Count -ne 3 -or @($candidateFiles | Where-Object Name -NotLike '*-candidate-*.zip').Count) { throw 'Default Candidate behavior changed.' }
    $results.Add('default Candidate still accepts an opaque historical evidence reference')
    $rejectedOutput = Join-Path $fixtureRoot 'rejected-output'
    Assert-Rejected 'Stable entry point rejects historical text before delivery' {
        & $entry -OutputDirectory $rejectedOutput -ReleaseChannel Stable -UserAcceptancePath $legacy
    } 'legacy free-text'
    if (Test-Path -LiteralPath $rejectedOutput) { throw 'Rejected Stable evidence created a delivery directory.' }
    $stableOutput = Join-Path $fixtureRoot 'stable-output'
    & $entry -OutputDirectory $stableOutput -ReleaseChannel Stable -UserAcceptancePath $goodPath | Out-Null
    if (@(Get-ChildItem -LiteralPath $stableOutput -File).Count -ne 3) { throw 'Bound Stable fixture did not produce three ZIPs.' }
    $userReceipts = @(Get-ChildItem -LiteralPath (Join-Path $fixtureArtifacts 'user-packages') -Filter 'user-package-receipt.json' -Recurse | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    })
    $stableReceipt = @($userReceipts | Where-Object Status -CEQ 'stable')
    if ($stableReceipt.Count -ne 1 -or $stableReceipt[0].Native -cne 'not-run' -or
        $stableReceipt[0].UserAcceptanceEvidence.Binding.Packages.Count -ne 3) { throw 'Stable fixture upgraded Native or omitted its evidence binding.' }
    $results.Add('bound Stable fixture delivers three ZIPs and keeps Native=not-run')
    if ((Get-FileHash -LiteralPath $buildSource -Algorithm SHA256).Hash -cne $buildHash) { throw 'Test changed its source build receipt.' }
    Write-Output "User-package acceptance regression checks passed: $($results.Count). No build, installation, game operation, or real user acceptance was performed."
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notlike 'rukari-acceptance-test-*') { throw 'Refusing cleanup outside the verified temporary fixture root.' }
    try { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force } catch {
        Write-Warning "Temporary fixture cleanup could not finish (for example, an external scanner holds a copied DLL): $resolvedFixture; $($_.Exception.Message)"
    }
}
