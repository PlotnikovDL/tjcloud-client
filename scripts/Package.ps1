[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$IsccPath,
    [switch]$ZipOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($name in @('TjCloudClient.exe', 'TjCloudClient.dll', 'TjClient.Core.dll', 'TjCloudClient.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $name) -PathType Leaf)) { throw "Incomplete publish: missing $name" }
}
$licenses = Join-Path $PublishDirectory 'licenses'
if (-not (Test-Path -LiteralPath $licenses -PathType Container) -or @(Get-ChildItem -LiteralPath $licenses -File -Recurse).Count -eq 0) { throw 'Incomplete publish: missing licenses directory contents.' }
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishDirectory 'TjCloudClient.exe'))
if ($info.FileVersion -ne "$($metadata.Version).0" -or $info.ProductName -ne $metadata.Product -or $info.CompanyName -ne $metadata.Publisher) {
    throw 'Published executable metadata does not match Directory.Build.props.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory already exists. Choose a fresh directory.' }
if (-not $ZipOnly) {
    if (-not $IsccPath) {
        $candidate = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
        if (Test-Path -LiteralPath $candidate) { $IsccPath = $candidate }
    }
    if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
        throw 'Inno Setup compiler not found. Supply -IsccPath or explicitly request -ZipOnly. No installer was produced.'
    }
}
New-Item -ItemType Directory $OutputDirectory | Out-Null
$zip = Join-Path $OutputDirectory "$($metadata.ArtifactStem).zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($PublishDirectory, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$artifacts = @($zip)
if (-not $ZipOnly) {
    $installerScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging/TjCloudClient.iss'
    & $IsccPath "/DAppVersion=$($metadata.Version)" "/DProductName=$($metadata.Product)" "/DPublisher=$($metadata.Publisher)" "/DRepositoryUrl=$($metadata.RepositoryUrl)" "/DPublishDirectory=$PublishDirectory" "/DOutputDirectory=$OutputDirectory" "/DArtifactStem=$($metadata.ArtifactStem)" $installerScript
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    $setup = Join-Path $OutputDirectory "$($metadata.ArtifactStem)-setup.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Compiler did not produce the expected installer.' }
    $artifacts += $setup
}
$lines = foreach ($artifact in $artifacts) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $artifact -Leaf)
}
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
$lines | Write-Host
Write-Host "Packages created: $OutputDirectory"
