param(
    [string]$OutputDirectory,
    [string]$ProjectName = 'Rukari-Isolated-Test'
)

<##
    Development-only synthetic AAP/AAS generator.
    It writes only to the requested output directory and never touches AA storage.
    The bytes are produced by the same synthetic builders used by the managed tests.
##>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$testAssemblyPath = Join-Path $repo 'AzureArchive.VideoTools.Tests/bin/Release/net6.0/AzureArchive.VideoTools.Tests.dll'
if (-not (Test-Path -LiteralPath $testAssemblyPath -PathType Leaf)) {
    throw "Build the managed tests first: $testAssemblyPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ("artifacts/isolated-test-data/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$root = [IO.Path]::GetFullPath($OutputDirectory)
$projects = Join-Path $root 'projects'
$saves = Join-Path $root 'saves'
$workspaceRoot = Join-Path $root 'AzureArchive.VideoTools/workspaces'
[IO.Directory]::CreateDirectory($projects) | Out-Null
[IO.Directory]::CreateDirectory($saves) | Out-Null
[IO.Directory]::CreateDirectory($workspaceRoot) | Out-Null

$assembly = [Reflection.Assembly]::LoadFrom($testAssemblyPath)
$aapType = $assembly.GetType('AzureArchive.VideoTools.Tests.SyntheticAap', $true)
$aasType = $assembly.GetType('AzureArchive.VideoTools.Tests.SyntheticAas', $true)
$recordType = $assembly.GetType('AzureArchive.VideoTools.Tests.SyntheticAasRecord', $true)

$sceneMethod = $aapType.GetMethod('Scene', [Reflection.BindingFlags]'Public,Static')
$projectMethod = $aapType.GetMethod('Project', [Reflection.BindingFlags]'Public,Static')
$archiveMethod = $aasType.GetMethod('Archive', [Reflection.BindingFlags]'Public,Static')
$scene = [string]$sceneMethod.Invoke($null, @(
        'Rukari isolated test dialogue',
        'ScriptData, Assembly-CSharp',
        $false,
        '#aavt;char;1;set;x=10;y=20;duration=0;easing=linear'))
$project = [string]$projectMethod.Invoke($null, @(
        [System.Collections.Generic.List[string]]@($scene),
        'ProjectData, Assembly-CSharp',
        '6fcb9e8d-a1e2-4bb4-a610-b59795bd1036'))

$recordConstructor = $recordType.GetConstructors()[0]
$record = $recordConstructor.Invoke(@(
        [long]123,
        [long]456,
        [long]999,
        'sound/test',
        [uint32]7,
        [uint32]8,
        [uint32]9,
        'popup/test',
        '1;character;00;dialogue',
        'Rukari isolated test dialogue',
        'thai',
        'traditional',
        'simplified',
        'english',
        'voice/test',
        $true))
$archive = [byte[]]$archiveMethod.Invoke($null, @($record))

$aapPath = Join-Path $projects ($ProjectName + '.aap')
$aasPath = Join-Path $saves ($ProjectName + '.aas')
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($aapPath, $project, $utf8)
[IO.File]::WriteAllBytes($aasPath, $archive)

$proofOutput = & dotnet $testAssemblyPath --write-command-proof-workspace `
    $aapPath $aasPath $workspaceRoot 0 '#char;1;set;x=10;y=20;duration=0;easing=linear' 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "Workspace proof generation failed: $proofOutput"
}

$receipt = [ordered]@{
    SchemaVersion = 1
    GeneratedAt = (Get-Date).ToString('o')
    Generator = 'SyntheticAap/SyntheticAas managed-test builders'
    ProjectName = $ProjectName
    Aap = [ordered]@{
        Path = $aapPath
        Length = (Get-Item -LiteralPath $aapPath).Length
        SHA256 = (Get-FileHash -LiteralPath $aapPath -Algorithm SHA256).Hash
    }
    Aas = [ordered]@{
        Path = $aasPath
        Length = (Get-Item -LiteralPath $aasPath).Length
        SHA256 = (Get-FileHash -LiteralPath $aasPath -Algorithm SHA256).Hash
    }
    Workspace = [ordered]@{
        Root = $workspaceRoot
        ProofOutput = $proofOutput.Trim()
    }
}
$receiptPath = Join-Path $root 'isolated-test-receipt.json'
Set-Content -LiteralPath $receiptPath -Value ($receipt | ConvertTo-Json -Depth 6) -Encoding UTF8

Write-Output "AAP: $aapPath"
Write-Output "AAS: $aasPath"
Write-Output "Receipt: $receiptPath"
