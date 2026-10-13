[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [string]$DataRoot,
    [string]$ProjectName,
    [string]$OutputDirectory
)

<##
    Read-only preflight for command-only AAVT dialogue rows.

    This script deliberately has no code path that starts AA. It checks the
    installation, inspects AAP/AAS files and existing logs, and writes a probe
    receipt outside the game directory. It does not save, compile, publish, or
    edit a project.
##>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
$game = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath $game -PathType Container)) {
    throw "Game root does not exist: $GameRoot"
}

$running = @(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $ids = ($running | ForEach-Object Id) -join ', '
    throw "AA is running (PID $ids). Close it before the offline probe; this script will never close or start AA."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ("artifacts/diagnostics/empty-dialogue-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null

$checks = [Collections.Generic.List[object]]::new()
$rows = [Collections.Generic.List[object]]::new()
$evidence = [Collections.Generic.List[object]]::new()

function Add-Check([string]$Name, [string]$Status, [string]$Detail) {
    $checks.Add([ordered]@{ Name = $Name; Status = $Status; Detail = $Detail })
}

function Add-File([string]$Label, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check $Label 'missing' $Path
        return
    }
    $item = Get-Item -LiteralPath $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $evidence.Add([ordered]@{
        Label = $Label
        Path = $Path
        Length = $item.Length
        LastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
        SHA256 = $hash
    })
    Add-Check $Label 'pass' ("length={0}; sha256={1}" -f $item.Length, $hash)
}

function Get-PropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Find-CommandOnlyRows($Value, [string]$Path = '$') {
    if ($null -eq $Value) { return }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Find-CommandOnlyRows $item ($Path + '[' + $index + ']')
            $index++
        }
        return
    }
    if ($Value -is [pscustomobject]) {
        $text = Get-PropertyValue $Value 'text'
        $prompt = Get-PropertyValue $Value 'additionalPrompt'
        if ($text -is [string] -and [string]::IsNullOrWhiteSpace($text) -and $prompt -is [string] -and
            $prompt -match '(?i)#(?:aavt|voice)\s*;') {
            $rows.Add([ordered]@{
                JsonPath = $Path
                Text = $text
                AdditionalPrompt = $prompt
                HasOfficialWait = ($prompt -match '(?i)#wait\s*;')
            })
        }
        foreach ($property in $Value.PSObject.Properties) {
            Find-CommandOnlyRows $property.Value ($Path + '.' + $property.Name)
        }
    }
}

function Inspect-Aap([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check 'aap' 'missing' $Path
        return
    }
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        $header = [Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(5, $bytes.Length))
        $offset = if ($header.StartsWith('AAP2')) { 5 } else { 0 }
        $json = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset) | ConvertFrom-Json
        Find-CommandOnlyRows $json
        Add-File 'aap' $Path
        Add-Check 'aap-command-only-rows' 'pass' ("rows={0}" -f $rows.Count)
    } catch {
        Add-Check 'aap-parse' 'error' ("$Path :: $($_.Exception.Message)")
    }
}

function Copy-LogEvidence([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check ("log:" + $Label) 'missing' $Path
        return
    }
    $text = Get-Content -LiteralPath $Path -Raw
    $matches = @($text -split "`r?`n" | Where-Object {
        $_ -match 'Empty AAVT dialogue anchor|AAVT command-only dialogue anchored|embedded-compile|index-load|SCENE_DISPATCH|EMPTY_FORMAL_ROW_FALLBACK|AmbiguousIdentity'
    })
    $copy = Join-Path $output (($Label -replace '[^A-Za-z0-9_.-]', '_') + '.log')
    Set-Content -LiteralPath $copy -Value $matches -Encoding UTF8
    $evidence.Add([ordered]@{ Label = 'log:' + $Label; Path = $Path; MatchedLines = $matches.Count; Copy = $copy })
    Add-Check ('log:' + $Label) 'pass' ("matchedLines={0}; copy={1}" -f $matches.Count, $copy)
}

foreach ($relative in @(
        'BepInEx/LogOutput.log',
        'mods/RukariLib/0.4.3/Rukari.Lib.dll',
        'mods/RukariLib/0.4.3/Rukari.Lib.Runtime.dll',
        'mods/更多的画面效果/1.6.5/AzureArchive.VideoTools.Core.dll',
        'mods/更多的画面效果/1.6.5/AzureArchive.VideoTools.Formats.dll',
        'mods/更多的画面效果/1.6.5/Rukari.MoreEffects.dll',
        'mods/人物配音支持/1.6.8/Rukari.CharacterVoice.dll')) {
    Add-File ('game:' + $relative) (Join-Path $game $relative)
}

Copy-LogEvidence (Join-Path $game 'BepInEx/LogOutput.log') 'BepInEx-LogOutput'

if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    $data = (Resolve-Path -LiteralPath $DataRoot).Path
    $projects = Join-Path $data 'projects'
    $saves = Join-Path $data 'saves'
    if (-not [string]::IsNullOrWhiteSpace($ProjectName)) {
        $aapCandidates = @(
            (Join-Path $projects ($ProjectName + '.aap2')),
            (Join-Path $projects ($ProjectName + '.aap'))
        )
        $aap = $aapCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($null -ne $aap) { Inspect-Aap $aap } else { Add-Check 'aap' 'missing' (Join-Path $projects ($ProjectName + '.[aap2|aap]')) }
        $aas = Join-Path $saves ($ProjectName + '.aas')
        Add-File 'aas' $aas
    } else {
        Add-Check 'project' 'not-provided' 'Pass -ProjectName to inspect command-only rows in one project.'
    }
} else {
    Add-Check 'data-root' 'not-provided' 'Pass -DataRoot and -ProjectName for AAP/AAS inspection.'
}

$receipt = [ordered]@{
    SchemaVersion = 1
    CreatedAt = (Get-Date).ToString('o')
    ReadOnly = $true
    AAProcessStarted = $false
    GameRoot = $game
    DataRoot = $DataRoot
    ProjectName = $ProjectName
    Checks = $checks
    CommandOnlyRows = $rows
    Evidence = $evidence
}
$receiptPath = Join-Path $output 'empty-dialogue-probe.json'
Set-Content -LiteralPath $receiptPath -Value ($receipt | ConvertTo-Json -Depth 12) -Encoding UTF8

$failed = @($checks | Where-Object Status -in @('fail', 'error')).Count
@(
    'Rukari offline empty-dialogue probe'
    ('AA process started: {0}' -f $receipt.AAProcessStarted)
    ('GameRoot: {0}' -f $game)
    ('Project: {0}' -f $ProjectName)
    ('Command-only AAP rows: {0}' -f $rows.Count)
    ('Failed checks: {0}' -f $failed)
    ('Receipt: {0}' -f $receiptPath)
) | Set-Content -LiteralPath (Join-Path $output 'summary.txt') -Encoding UTF8

Write-Output ("Offline probe written: {0}" -f $output)
if ($failed -gt 0) { exit 1 }
exit 0
