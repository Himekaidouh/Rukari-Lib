param(
    [switch]$TrackedOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    $files = if ($TrackedOnly) {
        @(git ls-files)
    }
    else {
        @(
            git ls-files --cached --others --exclude-standard
        )
    }

    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed.'
    }

    $patterns = [ordered]@{
        OpenAI = '(?i)\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}\b'
        GitHub = '(?i)\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,})\b'
        AWS = '\bAKIA[0-9A-Z]{16}\b'
        Google = '\bAIza[0-9A-Za-z_-]{30,}\b'
        Slack = '(?i)\bxox[baprs]-[A-Za-z0-9-]{10,}\b'
        Stripe = '(?i)\bsk_live_[A-Za-z0-9]{16,}\b'
        JWT = '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b'
        PrivateKey = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        AzureConnection = '(?i)\b(?:AccountKey|SharedAccessSignature)\s*='
        PasswordUrl = '://[^/\s@]+:[^/\s@]+@'
        CredentialAssignment = '(?i)\b(?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|password|passwd|credential)\s*[:=]\s*["'']?[A-Za-z0-9_./+=-]{8,}'
    }

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $files | Sort-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        $path = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.Length -gt 4MB -or $bytes -contains 0) {
            continue
        }

        $lines = [System.IO.File]::ReadAllLines($path)
        for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
            foreach ($entry in $patterns.GetEnumerator()) {
                if ([System.Text.RegularExpressions.Regex]::IsMatch(
                        $lines[$lineIndex],
                        $entry.Value)) {
                    $findings.Add(
                        "$relativePath`:$($lineIndex + 1): possible $($entry.Key) credential")
                }
            }
        }
    }

    if ($findings.Count -ne 0) {
        $findings | Sort-Object -Unique | Write-Error
        exit 1
    }

    Write-Output "Secret scan passed: $($files.Count) repository files checked."
}
finally {
    Pop-Location
}
