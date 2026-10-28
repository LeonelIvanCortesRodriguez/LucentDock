param(
    [Parameter(Mandatory)] [string]$Out,
    [string]$Exe = (Join-Path $PSScriptRoot "..\dist\Strata.exe"),
    # Code signing: the thumbprint of a code-signing certificate in Cert:\CurrentUser\My (or LocalMachine\My).
    # CI supplies only a certificate-store selector. Private keys and passwords are never command-line parameters.
    [string]$CertThumbprint = $env:STRATA_SIGNING_CERT_THUMBPRINT,
    # RFC 3161 timestamp server, so the signature outlives the certificate (for example your CA's).
    [string]$TimestampServer = $env:STRATA_TIMESTAMP_SERVER,
    [string]$Notes = ""
)
# Makes a Strata release folder that Strata's updater accepts (Settings > About > Updates):
#   Strata.exe     the build, signed when a certificate is given;
#   update.json    version, file, SHA-256 and size of that exact file, and notes;
#   release-notes.txt.
# Strata installs a release only if its SHA-256 matches update.json and, when the running Strata is signed, the
# release is validly signed by the same certificate. Without a trusted certificate the release is unsigned.
$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$version = (Get-Item $Exe).VersionInfo.ProductVersion
if (-not $version) { throw "$Exe has no ProductVersion" }
New-Item -ItemType Directory -Force $Out | Out-Null
$target = Join-Path $Out "Strata.exe"
Copy-Item $Exe $target -Force

$signed = $false
if ($CertThumbprint) {
    if (-not $TimestampServer) { throw "A timestamp server is required for signed release artifacts." }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$timestampUri) -or $timestampUri.Scheme -ne 'https') {
        throw "TimestampServer must be an https address."
    }
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object {
        $_.Thumbprint -eq $CertThumbprint -and
        @($_.EnhancedKeyUsageList | ForEach-Object ObjectId) -contains '1.3.6.1.5.5.7.3.3'
    } | Select-Object -First 1
    if (-not $cert) { throw "No code-signing certificate with thumbprint $CertThumbprint" }
    $signArgs = @{ FilePath = $target; Certificate = $cert; HashAlgorithm = "SHA256" }
    if ($TimestampServer) { $signArgs.TimestampServer = $TimestampServer }
    $sig = Set-AuthenticodeSignature @signArgs
    if ($sig.Status -ne "Valid") { throw "Signing failed: $($sig.Status) $($sig.StatusMessage)" }
    $signed = $true
}

$file = Get-Item $target
$stream = [IO.File]::OpenRead($target)
try {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $sha = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '') }
    finally { $sha256.Dispose() }
}
finally { $stream.Dispose() }
[ordered]@{ version = $version; file = "Strata.exe"; sha256 = $sha; size = $file.Length; channel = $(if ($signed) { "signed" } else { "unsigned-preview" }); notes = $Notes } |
    ConvertTo-Json | ForEach-Object { [IO.File]::WriteAllText((Join-Path $Out "update.json"), $_, (New-Object System.Text.UTF8Encoding $false)) }
# Written without a byte-order mark on any PowerShell: Strata's web check reads the bytes as they are.
$lines = @("Strata $version", "SHA-256 $sha", "Size $($file.Length) bytes", $(if ($signed) { "Signed and timestamped by $($cert.Subject)" } else { "UNSIGNED PREVIEW - not a public release" }))
if ($Notes) { $lines += ""; $lines += $Notes }
$lines | Set-Content (Join-Path $Out "release-notes.txt") -Encoding utf8
[pscustomobject]@{ Version = $version; Sha256 = $sha; Size = $file.Length; Signed = $signed; Folder = (Resolve-Path $Out).Path }
