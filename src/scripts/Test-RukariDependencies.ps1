param(
    [Parameter(Mandatory)][string]$GameRoot,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$InstallationRoot,
    [string]$OutputPath,
    [ValidateSet('All','FirstRelease','StableModules')][string]$Scope = 'StableModules'
)
# Read metadata only. Do not load plugin assemblies or initialize their IL2CPP types.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$core = Join-Path $GameRoot 'BepInEx\core'
foreach ($dll in @('Mono.Cecil.dll','SemanticVersioning.dll','BepInEx.Core.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $core $dll))
}
# Exercise the ACTUAL loader's version parser, including the exact-version regression from 2026-09-25.
$exact = [BepInEx.BepInDependency]::new('rukari.lib.runtime','0.2.2')
$range = [BepInEx.BepInDependency]::new('rukari.lib.runtime','>=0.2.4 <0.3.0')
if ($exact.VersionRange.IsSatisfied('0.2.4',$false,$false) -or
    -not $range.VersionRange.IsSatisfied('0.2.4',$false,$false) -or
    -not $range.VersionRange.IsSatisfied('0.2.5',$false,$false) -or
    $range.VersionRange.IsSatisfied('0.2.3',$false,$false) -or
    $range.VersionRange.IsSatisfied('0.3.0',$false,$false)) { throw 'Unexpected BepInEx dependency semantics.' }

$definitions = @(
    @{ Project='Rukari.Lib.Runtime'; Name='RukariLib'; Guid='rukari.lib.runtime' },
    @{ Project='Rukari.CharacterVoice'; Name='人物配音支持'; Guid='rukari.charactervoice' },
    @{ Project='Rukari.MoreEffects'; Name='更多的画面效果'; Guid='rukari.moreeffects' },
    @{ Project='Rukari.SpineSupport'; Name='更多Spine动画支持'; Guid='rukari.spinesupport' }
)
if ($Scope -eq 'StableModules') { $definitions = @($definitions | Where-Object Project -ne 'Rukari.Captions') }
if ($Scope -eq 'FirstRelease') {
    $definitions = @($definitions | Where-Object Name -in @('RukariLib','更多的画面效果','人物配音支持'))
}
$libVersion = [string](Get-Content -LiteralPath (Join-Path $repo 'Rukari.Lib.Runtime/package/manifest.json') -Raw | ConvertFrom-Json).version_number
if ($InstallationRoot) {
    $profile = (Get-Content -LiteralPath (Join-Path $InstallationRoot 'ActiveProfile.txt') -Raw).Trim()
    $enabled = (Get-Content -LiteralPath (Join-Path $InstallationRoot "profiles\$profile\modconfig.json") -Raw | ConvertFrom-Json).EnabledMods
    if ($Scope -eq 'FirstRelease' -and ($enabled.Count -ne 3 -or
        @(Compare-Object @($definitions.Name) @($enabled.name)).Count)) { throw 'Expected exactly the three first-release modules.' }
    $libVersion = [string]($enabled | Where-Object name -eq RukariLib).version
}
$failures = [Collections.Generic.List[string]]::new()
$boundaryRecords = [Collections.Generic.List[object]]::new()
$records = @()
foreach ($definition in $definitions) {
    if ($InstallationRoot) {
        $entry = @($enabled | Where-Object name -eq $definition.Name)
        if ($entry.Count -ne 1) { throw "Expected one active $($definition.Name) package." }
        $folder = Join-Path $InstallationRoot ('mods\' + $entry[0].name + '\' + $entry[0].version)
        $manifest = Get-Content -LiteralPath (Join-Path $folder 'manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.version_number -cne $entry[0].version) { $failures.Add("Profile/manifest version mismatch: $folder") }
        $dllPath = Join-Path $folder ($definition.Project + '.dll')
    } else {
        $dllPath = Join-Path $repo ($definition.Project + "\bin\$Configuration\" + $definition.Project + '.dll')
        $manifest = Get-Content -LiteralPath (Join-Path $repo ($definition.Project + '\package\manifest.json')) -Raw | ConvertFrom-Json
    }
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dllPath)
    try {
        $attributes = @($assembly.MainModule.Types | ForEach-Object CustomAttributes)
        $plugin = @($attributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
        if ($plugin.Count -ne 1) { throw "Expected one plugin declaration in $dllPath" }
        $guid = [string]$plugin[0].ConstructorArguments[0].Value
        $version = [string]$plugin[0].ConstructorArguments[2].Value
        if ($definition.Project -eq 'Rukari.SpineSupport' -and [version]$version -ge [version]'1.4.3') {
            $excludedTypes = @('SpineOverlayToolPage','SpineOverlayTrialBehaviour','SpineOverlayTrialRuntime')
            if (@($assembly.MainModule.Types | Where-Object { $_.Name -in $excludedTypes }).Count -ne 0) {
                $failures.Add('This source distribution must not contain the experimental overlay console or trial code.')
            }
        }
        $requirement = $null
        $satisfied = $true
        if ($definition.Name -ne 'RukariLib') {
            $dependency = @($attributes | Where-Object {
                $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments[0].Value -eq 'rukari.lib.runtime'
            })
            if ($dependency.Count -ne 1 -or $dependency[0].ConstructorArguments.Count -ne 2 -or
                $dependency[0].ConstructorArguments[1].Type.FullName -ne 'System.String') { throw "Missing versioned BepInEx dependency: $dllPath" }
            $requirement = [string]$dependency[0].ConstructorArguments[1].Value
            $parsed = [BepInEx.BepInDependency]::new('rukari.lib.runtime',$requirement)
            if ($definition.Project -in @('Rukari.CharacterVoice','Rukari.MoreEffects')) {
                # Check the declaration read from the actual DLL with the installed loader's parser.
                foreach ($boundary in @(
                    @{Version='0.4.1';Expected=$false},
                    @{Version='0.4.2';Expected=$true},
                    @{Version='0.5.0';Expected=$false}
                )) {
                    $accepted = $parsed.VersionRange.IsSatisfied($boundary.Version,$false,$false)
                    $boundaryRecords.Add([ordered]@{Name=$definition.Name;DllRequirement=$requirement;
                        LibVersion=$boundary.Version;Expected=$boundary.Expected;Accepted=$accepted})
                    if ($accepted -ne $boundary.Expected) {
                        $failures.Add("$($definition.Name): dependency '$requirement' has an unexpected result for lib $($boundary.Version)")
                    }
                }
            }
            $satisfied = $parsed.VersionRange.IsSatisfied($libVersion,$false,$false)
            if (-not $satisfied) { $failures.Add("$($definition.Name): DLL requires '$requirement', selected lib is $libVersion") }
            if (@($manifest.dependencies).Count -ne 1 -or $manifest.dependencies[0] -cne "RukariLib/$libVersion") {
                $failures.Add("$($definition.Name): official manifest does not require selected lib $libVersion")
            }
        }
        if ($guid -cne $definition.Guid) { $failures.Add("Unexpected plugin GUID in $dllPath : $guid") }
        if ($manifest.name -cne $definition.Name -or $manifest.version_number -cne $version) {
            $failures.Add("Manifest and DLL plugin identity disagree: $($definition.Name) / $version")
        }
        $records += [ordered]@{ Name=$definition.Name; PluginGuid=$guid; PluginVersion=$version;
            DllRequirement=$requirement; SelectedLib=$libVersion; Satisfied=$satisfied;
            DLL=$dllPath; SHA256=(Get-FileHash -LiteralPath $dllPath).Hash }
    } finally { $assembly.Dispose() }
}
$report = [ordered]@{ LoaderCoreSHA256=(Get-FileHash (Join-Path $core 'BepInEx.Core.dll')).Hash;
    Checks=$records; BoundaryChecks=@($boundaryRecords.ToArray()); Failures=@($failures.ToArray()); Native='not-run' }
if ($OutputPath) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output "PASS compiled dependency declarations: $($records.Count) plugins, official manifests and actual BepInEx version ranges agree."
