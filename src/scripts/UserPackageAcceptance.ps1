#requires -Version 7.0
# Stable-release evidence binding only. Loading this file performs no packaging or native validation.
. (Join-Path $PSScriptRoot 'PackagingReadRetry.ps1')

function Get-AcceptanceString($Record, [string]$Name) {
    if ($Record -isnot [Collections.IDictionary] -or -not $Record.Contains($Name) -or
        $Record[$Name] -isnot [string] -or [string]::IsNullOrWhiteSpace($Record[$Name])) {
        throw "Stable acceptance requires a nonempty string field: $Name"
    }
    $Record[$Name]
}

function Assert-AcceptanceHash([string]$Value, [string]$Field) {
    if ($Value -cnotmatch '^[A-Fa-f0-9]{64}$') { throw "Invalid Stable acceptance SHA256: $Field" }
}

function Read-UserPackageAcceptance([string]$Path) {
    # Parse and hash exactly the same bytes, rather than reading a mutable evidence file twice.
    $bytes = (Invoke-PackagingReadWithRetry { [pscustomobject]@{Bytes=[IO.File]::ReadAllBytes($Path)} }).Bytes
    try {
        $jsonText = [Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xFEFF)
        $document = $jsonText | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        # PowerShell versions may coerce ISO JSON strings into DateTime. Keep
        # the original timestamp and offset, and require an actual JSON string.
        $json = [Text.Json.JsonDocument]::Parse($jsonText)
        try {
            $timestamp = [Text.Json.JsonElement]::new()
            if ($json.RootElement.ValueKind -eq [Text.Json.JsonValueKind]::Object -and
                $json.RootElement.TryGetProperty('ConfirmedAt',[ref]$timestamp) -and
                $timestamp.ValueKind -eq [Text.Json.JsonValueKind]::String) {
                $document['ConfirmedAt'] = $timestamp.GetString()
            }
        } finally { $json.Dispose() }
    } catch {
        throw 'Stable acceptance must be SchemaVersion 1 structured JSON; legacy free-text evidence cannot approve a new release.'
    }
    if ($document -isnot [Collections.IDictionary] -or -not $document.Contains('SchemaVersion') -or
        ($document.SchemaVersion -isnot [int] -and $document.SchemaVersion -isnot [long]) -or
        $document.SchemaVersion -ne 1) {
        throw 'Stable acceptance requires SchemaVersion 1 structured JSON; historical acceptance is not automatically reusable.'
    }
    if ((Get-AcceptanceString $document 'Kind') -cne 'user-package-acceptance' -or
        (Get-AcceptanceString $document 'Target') -cne 'AA 1.0 fix6' -or
        (Get-AcceptanceString $document 'ToolNativeValidation') -cne 'not-run') {
        throw 'Stable acceptance kind/target/tool-native status is invalid; human approval never upgrades Native=not-run.'
    }
    if (-not $document.Contains('Approved') -or $document.Approved -isnot [bool] -or -not $document.Approved) {
        throw 'Stable acceptance requires the JSON boolean Approved=true from recorded human confirmation.'
    }
    $confirmedAt = Get-AcceptanceString $document 'ConfirmedAt'
    $parsedTime = [DateTimeOffset]::MinValue
    if ($confirmedAt -cnotmatch '^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$' -or
        -not [DateTimeOffset]::TryParse($confirmedAt,[Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,[ref]$parsedTime)) {
        throw 'Stable acceptance ConfirmedAt must be an ISO timestamp with an explicit offset.'
    }
    [void](Get-AcceptanceString $document 'HumanScope')
    [void](Get-AcceptanceString $document 'Evidence')
    Assert-AcceptanceHash (Get-AcceptanceString $document 'BuildReceiptSHA256') 'BuildReceiptSHA256'
    if (-not $document.Contains('Packages') -or $document.Packages -isnot [array] -or $document.Packages.Count -ne 3) {
        throw 'Stable acceptance must identify exactly three user packages.'
    }
    [pscustomobject]@{Document=$document;SHA256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))}
}

function Assert-UserPackageAcceptanceBinding($Acceptance, $PackageReceipt, [string]$BuildReceiptPath, [array]$Definitions) {
    $document = $Acceptance.Document
    $buildBytes = (Invoke-PackagingReadWithRetry { [pscustomobject]@{Bytes=[IO.File]::ReadAllBytes($BuildReceiptPath)} }).Bytes
    $buildHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buildBytes))
    $build = [Text.UTF8Encoding]::new($false,$true).GetString($buildBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    if ($PackageReceipt.BuildReceiptSHA256 -ine $buildHash -or $document.BuildReceiptSHA256 -ine $buildHash) {
        throw 'Stable acceptance build receipt SHA256 does not match this validated package build.'
    }
    if ($PackageReceipt.Native -cne 'not-run' -or $PackageReceipt.Target -cne 'Release10' -or
        $PackageReceipt.Configuration -cne 'Release') {
        throw 'Stable acceptance requires the validated Release10/Release stage; Native=not-run remains unchanged.'
    }
    if ($build.Native -cne 'not-run') { throw 'Human acceptance cannot rewrite the build receipt Native status.' }
    $seenPackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $bindings = [Collections.Generic.List[object]]::new()
    foreach ($accepted in $document.Packages) {
        $name = Get-AcceptanceString $accepted 'Name'
        $version = Get-AcceptanceString $accepted 'Version'
        if (-not $seenPackages.Add($name)) { throw "Duplicate Stable acceptance package: $name" }
        $definition = @($Definitions | Where-Object Name -CEQ $name)
        $package = @($PackageReceipt.Packages | Where-Object Name -CEQ $name)
        if ($definition.Count -ne 1 -or $package.Count -ne 1 -or $version -cne $package[0].Version -or
            $package[0].Folder -cne ('mods/' + $name + '/' + $version)) {
            throw "Stable acceptance package/version does not match this validated stage: $name/$version"
        }
        if (-not $accepted.Contains('Dlls') -or $accepted.Dlls -isnot [array] -or
            $accepted.Dlls.Count -ne $definition[0].Dlls.Count) {
            throw "Stable acceptance must bind every distributable DLL exactly once: $name"
        }
        $seenDlls = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($dll in $accepted.Dlls) {
            $path = Get-AcceptanceString $dll 'Path'
            $hash = Get-AcceptanceString $dll 'SHA256'
            Assert-AcceptanceHash $hash $path
            $expected = @($definition[0].Dlls | ForEach-Object { $package[0].Folder + '/' + $_ })
            if ($expected -cnotcontains $path -or -not $seenDlls.Add($path)) {
                throw "Unexpected or duplicate Stable acceptance DLL path: $path"
            }
            $file = @($PackageReceipt.Files | Where-Object Path -CEQ $path)
            if ($file.Count -ne 1 -or $hash -ine $file[0].SHA256) {
                throw "Stable acceptance DLL SHA256 does not match this validated stage: $path"
            }
            # The same hash must also occur on this DLL's unique recorded build output.
            $output = @($build.Outputs | Where-Object { [IO.Path]::GetFileName($_.Path) -ceq [IO.Path]::GetFileName($path) })
            if ($output.Count -ne 1 -or $hash -ine $output[0].SHA256) {
                throw "Stable acceptance DLL is not bound to its build output: $path"
            }
        }
        $bindings.Add([pscustomobject]@{Name=$name;Version=$version;Dlls=$accepted.Dlls})
    }
    if ($Definitions.Count -ne 3 -or @($Definitions | Where-Object { -not $seenPackages.Contains($_.Name) }).Count) {
        throw 'Stable acceptance omits one of the three current user packages.'
    }
    [pscustomobject]@{SchemaVersion=1;BuildReceiptSHA256=$buildHash;Packages=$bindings.ToArray()
        ConfirmedAt=$document.ConfirmedAt;HumanScope=$document.HumanScope;Evidence=$document.Evidence;ToolNativeValidation='not-run'}
}
