Set-StrictMode -Version Latest

function Get-PackageMetadata {
    $repo = Split-Path $PSScriptRoot -Parent
    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $group = $props.Project.PropertyGroup
    $version = [string]$group.Version
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid package version.' }
    foreach ($part in $version.Split('.')) { if ([long]$part -gt 65535) { throw 'Version component exceeds Windows file version limit.' } }
    $product = [string]$group.Product
    $publisher = [string]$group.Company
    foreach ($text in @($product, $publisher)) {
        if ($text -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,99}$') { throw 'Invalid product or publisher metadata.' }
    }
    $repository = [string]$group.RepositoryUrl
    if ($repository -cnotmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository URL.' }
    $installer = Get-Content -LiteralPath (Join-Path $repo 'packaging/TjCloudClient.iss') -Raw
    if ($installer -notmatch '(?m)^AppId=\{(\{[0-9a-fA-F-]{36}\})\r?$') { throw 'Missing fixed Inno AppId.' }
    $appId = $Matches[1]
    [void][guid]::Parse($appId)
    [pscustomobject]@{
        Version = $version
        Product = $product
        Publisher = $publisher
        RepositoryUrl = $repository
        License = [string]$group.PackageLicenseExpression
        Identifier = 'TJCloud.Client'
        AppId = $appId
        ProductCode = "${appId}_is1"
        ArtifactStem = "TJCloudClient-$version-win-x64"
    }
}
