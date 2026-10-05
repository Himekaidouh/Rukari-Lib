#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackagingReadRetry.ps1')
if (-not $IsWindows) { throw 'The sharing/byte-range lock fixture requires Windows.' }
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$fixtureRoot = Join-Path $tempBase ('rukari-read-retry-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$path = Join-Path $fixtureRoot 'payload.bin'
[IO.File]::WriteAllBytes($path,[byte[]](0..255))
$expected = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
$results = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)
$lockScript = Join-Path $fixtureRoot 'hold-lock.ps1'
$lockBody = @'
param([string]$Path,[string]$ReadyPath,[string]$Kind)
$ErrorActionPreference='Stop'
$stream=$null
try {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    if($Kind -eq 'range') {
        $stream.Dispose()
        $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $stream.Lock(0,$stream.Length)
    }
    [IO.File]::WriteAllText($ReadyPath,'ready')
    Start-Sleep -Milliseconds 700
} finally { if($null -ne $stream) { $stream.Dispose() } }
'@
[IO.File]::WriteAllText($lockScript,$lockBody,$utf8)

function Start-FixtureLock([string]$Kind) {
    $ready = Join-Path $fixtureRoot ([guid]::NewGuid().ToString('N') + '.ready')
    $arguments = @('-NoProfile','-File',('"' + $lockScript + '"'),'-Path',('"' + $path + '"'),
        '-ReadyPath',('"' + $ready + '"'),'-Kind',$Kind)
    $child = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $children.Add($child)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($child.HasExited -or $deadline.ElapsedMilliseconds -gt 5000) { throw 'Fixture lock helper failed before acquiring the file lock.' }
        Start-Sleep -Milliseconds 10
    }
    $observedCode = 0
    try { [IO.File]::ReadAllBytes($path) | Out-Null } catch {
        $cause = $_.Exception
        while ($null -ne $cause -and $cause -isnot [IO.IOException]) { $cause = $cause.InnerException }
        if ($null -ne $cause) { $observedCode = $cause.HResult -band 0xFFFF }
    }
    $requiredCode = if ($Kind -eq 'range') { 33 } else { 32 }
    if ($observedCode -ne $requiredCode) { throw "Fixture did not expose the intended Win32 lock error $requiredCode (observed $observedCode)." }
}
function Assert-LockFailure([string]$Name, [scriptblock]$Operation) {
    $failed = $false
    try { & $Operation | Out-Null } catch {
        if (-not (Test-PackagingSharingViolation $_.Exception)) { throw }
        $failed = $true
    }
    if (-not $failed) { throw "Expected a lock failure: $Name" }
    $results.Add($Name)
}
function New-FixtureZip([scriptblock]$Write) {
    $memory = [IO.MemoryStream]::new()
    try {
        $zip = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
        try { & $Write $zip } finally { $zip.Dispose() }
        $memory.Position = 0
        $read = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            if ($read.Entries.Count -ne 1) { throw 'ZIP retry left a duplicate or missing entry.' }
            $entryStream = $read.Entries[0].Open()
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($hash.ComputeHash($entryStream)) }
            finally { $hash.Dispose();$entryStream.Dispose() }
            if ($actual -cne $expected) { throw 'ZIP retry changed its verified payload.' }
        } finally { $read.Dispose() }
    } finally { $memory.Dispose() }
}

try {
    Start-FixtureLock 'share'
    $actual = Invoke-PackagingReadWithRetry -TimeoutMilliseconds 3000 -Operation {
        (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
    }
    if ($actual -cne $expected) { throw 'Hash retry did not preserve bytes.' }
    $results.Add('hash succeeds after real sharing lock is released')
    Start-FixtureLock 'share'
    New-FixtureZip { param($zip) Add-PackagingZipEntry $zip $path 'payload.bin' $expected -TimeoutMilliseconds 3000 }
    $results.Add('ZIP succeeds after sharing lock release with exactly one verified entry')
    Start-FixtureLock 'range'
    New-FixtureZip { param($zip) Add-PackagingZipEntry $zip $path 'payload.bin' $expected -TimeoutMilliseconds 3000 }
    $results.Add('ZIP succeeds after byte-range lock release with exactly one verified entry')

    $held = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        Assert-LockFailure 'persistent lock times out instead of bypassing the hash' {
            Invoke-PackagingReadWithRetry -TimeoutMilliseconds 250 -DelayMilliseconds 30 -Operation {
                (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
            }
        }
        if ($timer.ElapsedMilliseconds -gt 2000) { throw 'Short fixture retry deadline was exceeded.' }
        $memory = [IO.MemoryStream]::new()
        try {
            $zip = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
            try {
                Assert-LockFailure 'ZIP timeout leaves no partial entry' {
                    Add-PackagingZipEntry $zip $path 'payload.bin' $expected -TimeoutMilliseconds 250
                }
            } finally { $zip.Dispose() }
            $memory.Position = 0
            $read = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read,$true)
            try { if ($read.Entries.Count -ne 0) { throw 'Failed ZIP input left a partial entry.' } } finally { $read.Dispose() }
        } finally { $memory.Dispose() }
    } finally { $held.Dispose() }

    $script:attemptCount = 0
    $failed = $false
    try {
        Invoke-PackagingReadWithRetry -TimeoutMilliseconds 1000 -Operation {
            $script:attemptCount++
            throw [IO.IOException]::new('fixture: ordinary IO error')
        }
    } catch {
        if ($_.Exception.Message -notlike '*ordinary IO error*') { throw }
        $failed = $true
    }
    if (-not $failed -or $script:attemptCount -ne 1) { throw 'Non-lock IOException was retried or hidden.' }
    $results.Add('non-lock IOException fails on its first attempt')
    if (Test-PackagingSharingViolation ([IO.IOException]::new('wrong facility',32))) { throw 'A low 32 without the Win32 HRESULT was treated as a sharing lock.' }
    $results.Add('only the exact Win32 32/33 HRESULTs qualify')
    $script:attemptCount = 0
    $failed = $false
    try {
        Invoke-PackagingReadWithRetry -TimeoutMilliseconds 1000 -Operation {
            $script:attemptCount++
            [IO.File]::ReadAllBytes((Join-Path $fixtureRoot 'missing.bin'))
        }
    } catch {
        if (Test-PackagingSharingViolation $_.Exception) { throw }
        $failed = $true
    }
    if (-not $failed -or $script:attemptCount -ne 1) { throw 'Missing input was retried or hidden.' }
    $results.Add('missing input remains an immediate failure')
    $memory = [IO.MemoryStream]::new()
    try {
        $zip = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
        $failed = $false
        try {
            try { Add-PackagingZipEntry $zip $path 'payload.bin' ('0' * 64) } catch {
                if ($_.Exception.Message -notlike '*ZIP input hash does not match*') { throw }
                $failed = $true
            }
        } finally { $zip.Dispose() }
        if (-not $failed) { throw 'ZIP input hash mismatch was hidden.' }
        $memory.Position = 0
        $read = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read,$true)
        try { if ($read.Entries.Count -ne 0) { throw 'Hash mismatch created an unverified entry.' } } finally { $read.Dispose() }
    } finally { $memory.Dispose() }
    $results.Add('ZIP snapshot hash mismatch fails before creating an entry')
    Write-Output "Packaging read-retry checks passed: $($results.Count). No build, package delivery, deployment, profile change, or game operation was performed."
} finally {
    foreach ($child in $children) { [void]$child.WaitForExit(5000);$child.Dispose() }
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($tempBase + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notlike 'rukari-read-retry-test-*') { throw 'Refusing cleanup outside the verified temporary fixture root.' }
    try { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force } catch { Write-Warning "Temporary read-retry fixture cleanup failed: $resolvedFixture" }
}
