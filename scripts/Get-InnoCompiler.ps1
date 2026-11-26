[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ToolRoot,
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ToolRoot = [IO.Path]::GetFullPath($ToolRoot)
$compiler = Join-Path $ToolRoot 'compiler'
if (Test-Path -LiteralPath $compiler) { throw 'Compiler directory already exists. Choose a fresh ToolRoot.' }
$expected = 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'
if (-not $ArchivePath) {
    New-Item -ItemType Directory -Force $ToolRoot | Out-Null
    $ArchivePath = Join-Path $ToolRoot 'innosetup-6.7.4.exe'
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $ArchivePath -UseBasicParsing -TimeoutSec 120
}
$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw 'Compiler archive SHA-256 mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $ArchivePath
if ($signature.Status -ne 'Valid') { throw 'Compiler Authenticode signature is not Valid.' }
$publisher = $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
if ($publisher -cne 'Pyrsys B.V.') { throw 'Unexpected compiler Authenticode publisher.' }
Write-Host "PASS compiler SHA-256 and Valid Authenticode from $publisher"
& gh release verify-asset is-6_7_4 $ArchivePath --repo jrsoftware/issrc
if ($LASTEXITCODE -ne 0) { throw 'Compiler GitHub release asset verification failed.' }
New-Item -ItemType Directory -Force $ToolRoot | Out-Null
$arguments = @('/CURRENTUSER', '/PORTABLE=1', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NORESTARTAPPLICATIONS', '/NOCLOSEAPPLICATIONS', '/NOICONS', "/DIR=`"$compiler`"")
$process = Start-Process -FilePath $ArchivePath -ArgumentList $arguments -PassThru
try {
    if (-not $process.WaitForExit(120000)) { throw 'Portable compiler extraction timed out.' }
    if ($process.ExitCode -ne 0) { throw "Portable compiler extraction failed: $($process.ExitCode)" }
}
finally { $process.Dispose() }
$iscc = Join-Path $compiler 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'Portable compiler was not extracted.' }
if (Test-Path -LiteralPath (Join-Path $compiler 'unins000.exe')) { throw 'Unexpected uninstaller in the portable tool directory.' }
Write-Host "Portable Inno Setup compiler: $iscc"
