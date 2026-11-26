[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$InstallerUrl,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SchemaOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
if ($Version -cne $metadata.Version) { throw 'Version must exactly match Directory.Build.props.' }
$expectedName = "$($metadata.ArtifactStem)-setup.exe"
$expectedUrl = "$($metadata.RepositoryUrl)/releases/download/v$Version/$expectedName"
if ($InstallerUrl -cne $expectedUrl) { throw 'InstallerUrl must be the exact HTTPS release asset URL for the approved repository and version, without credentials, query or fragment.' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
if ((Split-Path $InstallerPath -Leaf) -cne $expectedName) { throw 'Installer filename must match the versioned setup artifact name.' }
$stream = [IO.File]::OpenRead($InstallerPath)
try {
    if ($stream.Length -lt 64 -or $stream.ReadByte() -ne 77 -or $stream.ReadByte() -ne 90) { throw 'Installer must be a nonempty Windows executable.' }
}
finally { $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory already exists. Choose a fresh directory.' }
if (-not $SchemaOnly) {
    $download = Join-Path (Split-Path $InstallerPath -Parent) ('.winget-verify-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        Invoke-WebRequest -Uri $InstallerUrl -OutFile $download -UseBasicParsing -TimeoutSec 120 -MaximumRedirection 5
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -cne $hash) { throw 'Downloaded installer hash mismatch.' }
    }
    finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
}
$destination = Join-Path $OutputDirectory "manifests/t/TJCloud/Client/$Version"
$common = "PackageIdentifier: $($metadata.Identifier)`nPackageVersion: '$Version'"
if ($SchemaOnly) { $common = "# SCHEMA ONLY - URL not verified, do not submit or publish this fixture.`n$common" }
$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
$common
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.12.0
"@
$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
$common
PackageLocale: en-US
Publisher: '$($metadata.Publisher)'
PublisherUrl: '$($metadata.RepositoryUrl)'
PublisherSupportUrl: '$($metadata.RepositoryUrl)/issues'
PackageName: '$($metadata.Product)'
PackageUrl: '$($metadata.RepositoryUrl)'
License: '$($metadata.License)'
LicenseUrl: '$($metadata.RepositoryUrl)/blob/v$Version/LICENSE'
ShortDescription: Windows client for explicitly approved TJ Cloud log uploads.
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@
$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
$common
InstallerType: inno
Scope: user
MinimumOSVersion: 10.0.17763.0
UpgradeBehavior: install
InstallModes:
- interactive
- silent
- silentWithProgress
InstallerSwitches:
  Custom: /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS
AppsAndFeaturesEntries:
- DisplayName: '$($metadata.Product)'
  Publisher: '$($metadata.Publisher)'
  DisplayVersion: '$Version'
  ProductCode: '$($metadata.ProductCode)'
Installers:
- Architecture: x64
  InstallerUrl: '$InstallerUrl'
  InstallerSha256: $hash
  ProductCode: '$($metadata.ProductCode)'
ManifestType: installer
ManifestVersion: 1.12.0
"@
New-Item -ItemType Directory -Force $destination | Out-Null
$documents = @{
    "$($metadata.Identifier).yaml" = $versionYaml
    "$($metadata.Identifier).locale.en-US.yaml" = $localeYaml
    "$($metadata.Identifier).installer.yaml" = $installerYaml
}
foreach ($name in $documents.Keys) {
    [IO.File]::WriteAllText((Join-Path $destination $name), $documents[$name] + "`n", [Text.UTF8Encoding]::new($false))
}
Write-Host "Generated from file SHA-256 $hash"
Write-Host "Manifest directory: $destination"
if ($SchemaOnly) { Write-Warning 'SCHEMA ONLY. URL availability and downloaded bytes were not verified. Do not submit or publish.' }
Write-Host 'Generation does not install the application, submit to WinGet, or establish Microsoft approval.'
