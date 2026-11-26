[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$EventPath = $env:GITHUB_EVENT_PATH,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Ref = $env:GITHUB_REF
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($EventName -ceq 'pull_request') {
    $event = Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json
    $pr = $event.pull_request
    if ($pr.base.ref -ceq 'develop') {
        if ($pr.head.ref -cnotmatch '^feature/.+') { throw 'develop requires a feature/* head.' }
    }
    elseif ($pr.base.ref -ceq 'master') {
        if ($pr.head.ref -cne 'develop' -or [string]::IsNullOrWhiteSpace($Repository) -or
            $pr.head.repo.full_name -cne $Repository) {
            throw 'master requires develop from this repository.'
        }
    }
    else { throw 'Unsupported PR base.' }
}

if ($Ref -clike 'refs/tags/*') {
    [xml]$props = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $Ref -cne "refs/tags/v$version") {
        throw 'Release tag does not match Directory.Build.props.'
    }
    $tagCommit = & git -C $RepositoryRoot rev-parse --verify --quiet "$Ref^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve release tag.' }
    $masterCommit = & git -C $RepositoryRoot rev-parse --verify --quiet 'refs/remotes/origin/master^{commit}'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve origin/master.' }
    & git -C $RepositoryRoot merge-base --is-ancestor $tagCommit $masterCommit
    if ($LASTEXITCODE -ne 0) { throw 'Release tag is not reachable from origin/master.' }
}

Write-Host 'Branch policy passed.'
