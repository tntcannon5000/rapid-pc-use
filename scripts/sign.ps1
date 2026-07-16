[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Executable not found: $Executable"
}
if ([string]::IsNullOrWhiteSpace($env:RAPID_PC_USE_SIGNING_CERT_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:RAPID_PC_USE_SIGNING_CERT_PASSWORD)) {
    throw 'Release signing credentials are not configured.'
}

$temporaryPfx = Join-Path $env:RUNNER_TEMP ("rapid-pc-use-signing-{0}.pfx" -f [Guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllBytes($temporaryPfx, [Convert]::FromBase64String($env:RAPID_PC_USE_SIGNING_CERT_BASE64))
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $temporaryPfx,
        $env:RAPID_PC_USE_SIGNING_CERT_PASSWORD,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )
    if (-not $certificate.HasPrivateKey) {
        throw 'The release signing certificate has no private key.'
    }

    $signature = Set-AuthenticodeSignature `
        -LiteralPath $Executable `
        -Certificate $certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampServer
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed with status '$($signature.Status)'."
    }

    $verification = Get-AuthenticodeSignature -LiteralPath $Executable
    if ($verification.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $verification.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The signed executable did not pass Authenticode verification.'
    }

    Write-Host "Authenticode signature verified for $Executable" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryPfx) {
        Remove-Item -LiteralPath $temporaryPfx -Force
    }
}
