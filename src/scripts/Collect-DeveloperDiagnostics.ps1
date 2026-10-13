param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [string]$DataRoot,
    [string]$ProjectName,
    [string]$OutputDirectory,
    [string]$BaselineReceipt,
    [switch]$SkipManagedTests
)

<##
    Development-only, read-only diagnostic collector.

    This script deliberately does not start AA, install files, call the editor's
    Save/Compile methods, or write inside the supplied game directory. It gathers
    the evidence needed to distinguish a bad save/publish pair from a loading,
    mapping, ABI, or runtime problem. The output is an audit bundle, not a user
    package and is excluded from release packaging.
##>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath $game -PathType Container)) {
    throw "Game root does not exist: $GameRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ("artifacts/diagnostics/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $output 'logs')) | Out-Null

$checks = [Collections.Generic.List[object]]::new()
$files = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
$comparisons = [Collections.Generic.List[object]]::new()

function Add-Check([string]$Name, [string]$Status, [string]$Detail) {
    $checks.Add([ordered]@{ Name = $Name; Status = $Status; Detail = $Detail })
}

function Get-FileEvidence([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check $Label 'missing' $Path
        return
    }
    try {
        $item = Get-Item -LiteralPath $Path
        $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        $files.Add([ordered]@{
            Label = $Label
            Path = $Path
            Exists = $true
            Length = $item.Length
            LastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
            SHA256 = $hash
        })
        Add-Check $Label 'pass' ("length={0}; sha256={1}" -f $item.Length, $hash)
    } catch {
        Add-Check $Label 'error' ("$Path :: $($_.Exception.GetType().Name): $($_.Exception.Message)")
    }
}

function Add-DirectoryEvidence([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Add-Check $Label 'missing' $Path
        return
    }
    try {
        $count = @(Get-ChildItem -LiteralPath $Path -File -ErrorAction Stop).Count
        Add-Check $Label 'pass' ("path=$Path; directFiles=$count")
    } catch {
        Add-Check $Label 'error' ("$Path :: $($_.Exception.GetType().Name): $($_.Exception.Message)")
    }
}

function Copy-Tail([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check "log:$Label" 'missing' $Path
        return
    }
    try {
        $safe = ($Label -replace '[^A-Za-z0-9_.-]', '_') + '.tail.log'
        $destination = Join-Path $output "logs/$safe"
        $lines = @(Get-Content -LiteralPath $Path -Tail 4000 -ErrorAction Stop)
        Set-Content -LiteralPath $destination -Value $lines -Encoding UTF8
        $files.Add([ordered]@{
            Label = "log:$Label"
            Path = $Path
            TailCopy = $destination
            TailLines = $lines.Count
            Length = (Get-Item -LiteralPath $Path).Length
        })
        Add-Check "log:$Label" 'pass' ("tailLines={0}; copy={1}" -f $lines.Count, $destination)
    } catch {
        Add-Check "log:$Label" 'error' ("$Path :: $($_.Exception.GetType().Name): $($_.Exception.Message)")
    }
}

function Invoke-ManagedCheck([string]$Label, [string[]]$Arguments) {
    $testDll = Join-Path $repo 'AzureArchive.VideoTools.Tests/bin/Release/net6.0/AzureArchive.VideoTools.Tests.dll'
    if (-not (Test-Path -LiteralPath $testDll -PathType Leaf)) {
        Add-Check "managed:$Label" 'missing' "Build first: $testDll"
        return
    }
    $started = Get-Date
    $text = & dotnet $testDll @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $logPath = Join-Path $output ("managed-$Label.log")
    Set-Content -LiteralPath $logPath -Value $text -Encoding UTF8
    $commands.Add([ordered]@{
        Label = $Label
        Arguments = $Arguments
        ExitCode = $exitCode
        Started = $started.ToString('o')
        Output = $logPath
    })
    if ($exitCode -eq 0) { Add-Check "managed:$Label" 'pass' $logPath }
    else { Add-Check "managed:$Label" 'fail' ("exit=$exitCode; output=$logPath") }
}

function Get-RecordValue($Record, [string]$Name) {
    if ($Record -is [Collections.IDictionary]) {
        if ($Record.Contains($Name)) { return $Record[$Name] }
        return $null
    }
    $property = $Record.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Normalize-TimeValue($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return '' }
    try { return ([DateTimeOffset]::Parse([string]$Value)).ToUniversalTime().ToString('o') }
    catch { return [string]$Value }
}

function Compare-BaselineReceipt([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check 'baseline-receipt' 'error' "Receipt does not exist: $Path"
        return
    }
    try {
        $baseline = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json
        $baselineByLabel = @{}
        foreach ($entry in @($baseline.Files)) {
            $entryLabel = Get-RecordValue $entry 'Label'
            if ($null -ne $entryLabel) { $baselineByLabel[[string]$entryLabel] = $entry }
        }
        foreach ($entry in @($files)) {
            $label = [string](Get-RecordValue $entry 'Label')
            if (-not $baselineByLabel.ContainsKey($label)) { continue }
            $before = $baselineByLabel[$label]
            $beforeHashValue = Get-RecordValue $before 'SHA256'
            $afterHashValue = Get-RecordValue $entry 'SHA256'
            $beforeLengthValue = Get-RecordValue $before 'Length'
            $afterLengthValue = Get-RecordValue $entry 'Length'
            $beforeTimeValue = Get-RecordValue $before 'LastWriteUtc'
            $afterTimeValue = Get-RecordValue $entry 'LastWriteUtc'
            $beforeHash = if ($null -ne $beforeHashValue) { [string]$beforeHashValue } else { '' }
            $afterHash = if ($null -ne $afterHashValue) { [string]$afterHashValue } else { '' }
            $beforeLength = if ($null -ne $beforeLengthValue) { [int64]$beforeLengthValue } else { $null }
            $afterLength = if ($null -ne $afterLengthValue) { [int64]$afterLengthValue } else { $null }
            $beforeTime = Normalize-TimeValue $beforeTimeValue
            $afterTime = Normalize-TimeValue $afterTimeValue
            $sameHash = $beforeHash -eq $afterHash
            $sameLength = $beforeLength -eq $afterLength
            $sameTime = $beforeTime -eq $afterTime
            $sameContent = if ($beforeHash.Length -gt 0 -and $afterHash.Length -gt 0) {
                $sameHash -and $sameLength
            } else {
                $sameLength -and $sameTime
            }
            $comparisons.Add([ordered]@{
                Label = $label
                BeforePath = [string](Get-RecordValue $before 'Path')
                AfterPath = [string](Get-RecordValue $entry 'Path')
                BeforeLength = $beforeLength
                AfterLength = $afterLength
                BeforeLastWriteUtc = $beforeTime
                AfterLastWriteUtc = $afterTime
                BeforeSHA256 = $beforeHash
                AfterSHA256 = $afterHash
                Changed = -not $sameContent
            })
        }
        Add-Check 'baseline-receipt' 'pass' ("Compared {0} matching file record(s) from {1}" -f $comparisons.Count, $Path)
    } catch {
        Add-Check 'baseline-receipt' 'error' ("$Path :: $($_.Exception.GetType().Name): $($_.Exception.Message)")
    }
}

Add-Check 'game-root' 'pass' $game
foreach ($relative in @(
        'GameAssembly.dll',
        'UnityPlayer.dll',
        'BepInEx/core/BepInEx.Core.dll',
        'BepInEx/core/BepInEx.Unity.IL2CPP.dll',
        'BepInEx/core/Il2CppInterop.Runtime.dll',
        'BepInEx/core/0Harmony.dll',
        'BepInEx/interop/Assembly-CSharp.dll',
        'BepInEx/patchers/ModTheAzureArchive.dll')) {
    Get-FileEvidence (Join-Path $game $relative) "game:$relative"
}

$bep = Join-Path $game 'BepInEx'
if (Test-Path -LiteralPath $bep -PathType Container) {
    Copy-Tail (Join-Path $bep 'LogOutput.log') 'BepInEx-LogOutput'
    foreach ($log in @(Get-ChildItem -LiteralPath $bep -Recurse -File -Include *.log -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 8)) {
        Copy-Tail $log.FullName ("BepInEx-" + $log.BaseName)
    }
    foreach ($config in @(Get-ChildItem -LiteralPath $bep -Recurse -File -Include '*Rukari*.cfg','*Rukari*.json' -ErrorAction SilentlyContinue)) {
        Copy-Tail $config.FullName ("config-" + $config.BaseName)
    }
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    Add-Check 'data-root' 'not-provided' 'Pass -DataRoot to inspect projects and saves; no path is guessed.'
} else {
    $data = (Resolve-Path -LiteralPath $DataRoot).Path
    Add-Check 'data-root' 'pass' $data
    $projects = Join-Path $data 'projects'
    $saves = Join-Path $data 'saves'
    if (Test-Path -LiteralPath $projects -PathType Container) {
        Add-DirectoryEvidence $projects 'data:projects-directory'
        $projectNames = @(Get-ChildItem -LiteralPath $projects -File -Filter '*.aap' -ErrorAction Stop | ForEach-Object BaseName)
        if (-not [string]::IsNullOrWhiteSpace($ProjectName)) {
            $aap = Join-Path $projects ($ProjectName + '.aap')
            $aas = Join-Path $saves ($ProjectName + '.aas')
            Get-FileEvidence $aap 'project:aap'
            Get-FileEvidence $aas 'project:aas'
            if (-not ((Test-Path -LiteralPath $aap -PathType Leaf) -and (Test-Path -LiteralPath $aas -PathType Leaf))) {
                Add-Check 'project-pair' 'fail' 'AAP/AAS pair is incomplete; formal playback cannot be trusted.'
            }
        }
        if (-not $SkipManagedTests) { Invoke-ManagedCheck 'aap-directory' @('--aap-directory', $projects) }
    } else {
        Add-Check 'data:projects-directory' 'missing' $projects
    }
    if (Test-Path -LiteralPath $saves -PathType Container) {
        Add-DirectoryEvidence $saves 'data:saves-directory'
        $saveNames = @(Get-ChildItem -LiteralPath $saves -File -Filter '*.aas' -ErrorAction Stop | ForEach-Object BaseName)
        if (-not $SkipManagedTests) { Invoke-ManagedCheck 'aas-directory' @('--aas-directory', $saves) }
    } else {
        Add-Check 'data:saves-directory' 'missing' $saves
    }
    if ((Test-Path -LiteralPath $projects -PathType Container) -and
        (Test-Path -LiteralPath $saves -PathType Container)) {
        $missingAas = @($projectNames | Where-Object { $_ -notin $saveNames })
        $orphanAas = @($saveNames | Where-Object { $_ -notin $projectNames })
        if ($missingAas.Count -eq 0) {
            Add-Check 'data:pair-integrity' 'pass' ("AAP/AAS pairs complete; projects={0}; saves={1}" -f $projectNames.Count, $saveNames.Count)
        } else {
            $shown = ($missingAas | Select-Object -First 20) -join ', '
            Add-Check 'data:pair-integrity' 'fail' ("{0} AAP file(s) have no matching AAS: {1}" -f $missingAas.Count, $shown)
        }
        if ($orphanAas.Count -gt 0) {
            $shown = ($orphanAas | Select-Object -First 20) -join ', '
            Add-Check 'data:orphan-saves' 'warning' ("{0} AAS file(s) have no matching AAP: {1}" -f $orphanAas.Count, $shown)
        } else {
            Add-Check 'data:orphan-saves' 'pass' 'No orphan AAS files.'
        }
    }
    if (-not $SkipManagedTests -and
        (Test-Path -LiteralPath $projects -PathType Container) -and
        (Test-Path -LiteralPath $saves -PathType Container)) {
        Invoke-ManagedCheck 'mapping' @('--mapping-directories', $projects, $saves)
        Invoke-ManagedCheck 'workspace' @('--command-workspace-directories', $projects, $saves)
    }
}

if (-not $SkipManagedTests) { Invoke-ManagedCheck 'managed-suite' @() }

Compare-BaselineReceipt $BaselineReceipt

$receipt = [ordered]@{
    SchemaVersion = 1
    CreatedAt = (Get-Date).ToString('o')
    ReadOnly = $true
    GameRoot = $game
    DataRoot = $DataRoot
    ProjectName = $ProjectName
    BaselineReceipt = $BaselineReceipt
    Checks = $checks
    Files = $files
    Commands = $commands
    Comparisons = $comparisons
    SaveChainContract = [ordered]@{
        Sequence = @('official Save(false)', 'official Compile()', 'official Compile(projectName)')
        VerifiedByThisTool = $false
        Note = 'This bundle verifies files and parsers after the fact. It never calls the official save/compile methods.'
    }
}
$jsonPath = Join-Path $output 'diagnostic-receipt.json'
Set-Content -LiteralPath $jsonPath -Value ($receipt | ConvertTo-Json -Depth 8) -Encoding UTF8
$summaryPath = Join-Path $output 'diagnostic-summary.txt'
$failed = @($checks | Where-Object { $_.Status -in @('fail','error') }).Count
@(
    "Rukari developer diagnostics"
    "Created: $($receipt.CreatedAt)"
    "GameRoot: $game"
    "DataRoot: $DataRoot"
    "Project: $ProjectName"
    "Failed checks: $failed"
    "Receipt: $jsonPath"
) | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Output "Diagnostics written: $output"
if ($failed -gt 0) { exit 1 }
exit 0
