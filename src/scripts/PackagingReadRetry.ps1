#requires -Version 7.0
# Retry only reads blocked by Windows sharing/byte-range locks; never weaken validation.

function Test-PackagingSharingViolation([Exception]$Exception) {
    $cause = $Exception
    while ($null -ne $cause) {
        if ($cause -is [IO.IOException]) {
            # HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION=32 / ERROR_LOCK_VIOLATION=33).
            return $cause.HResult -eq -2147024864 -or $cause.HResult -eq -2147024863
        }
        $cause = $cause.InnerException
    }
    return $false
}

function Invoke-PackagingReadWithRetry {
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [ValidateRange(0,10000)][int]$TimeoutMilliseconds = 9000,
        [ValidateRange(1,250)][int]$DelayMilliseconds = 100
    )
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { return & $Operation } catch {
            if (-not (Test-PackagingSharingViolation $_.Exception)) { throw }
            $remaining = $TimeoutMilliseconds - [int][Math]::Ceiling($elapsed.Elapsed.TotalMilliseconds)
            if ($remaining -le 0) { throw }
            Start-Sleep -Milliseconds ([Math]::Min($DelayMilliseconds,$remaining))
            # Do not start another attempt after the bounded deadline expires.
            if ($elapsed.Elapsed.TotalMilliseconds -ge $TimeoutMilliseconds) { throw }
        }
    }
}

function Add-PackagingZipEntry {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$EntryName,
        [Parameter(Mandatory)][string]$ExpectedSHA256,
        [ValidateRange(0,10000)][int]$TimeoutMilliseconds = 9000
    )
    # CreateEntryFromFile creates an entry before copying source bytes. A lock
    # violation during that copy would leave a partial entry on a retry. Read
    # and validate a complete snapshot first, then create exactly one entry.
    $snapshot = Invoke-PackagingReadWithRetry -TimeoutMilliseconds $TimeoutMilliseconds -Operation {
        [pscustomobject]@{Bytes=[IO.File]::ReadAllBytes($Path);LastWriteTime=[IO.File]::GetLastWriteTime($Path)}
    }
    $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($snapshot.Bytes))
    if ($ExpectedSHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or $actualHash -ine $ExpectedSHA256) {
        throw "ZIP input hash does not match its receipt: $EntryName"
    }
    $entry = $Archive.CreateEntry($EntryName,[IO.Compression.CompressionLevel]::Optimal)
    $lastWrite = $snapshot.LastWriteTime
    if ($lastWrite.Year -lt 1980 -or $lastWrite.Year -gt 2107) { $lastWrite = [DateTime]::new(1980,1,1,0,0,0) }
    $entry.LastWriteTime = $lastWrite
    $entryStream = $entry.Open()
    try { $entryStream.Write($snapshot.Bytes,0,$snapshot.Bytes.Length) } finally { $entryStream.Dispose() }
}
