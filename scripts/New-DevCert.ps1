# Creates the self-signed certificate used to sign Stremio Community packages for sideloading.
# Run once on the Windows build PC from the repo root (xbox\):
#   powershell -ExecutionPolicy Bypass -File scripts\New-DevCert.ps1
# The subject must match Publisher in StremioCommunity\Package.appxmanifest.
$subject = "CN=StremioCommunityDev"
$password = ConvertTo-SecureString -String "stremio" -Force -AsPlainText
$pfxPath = Join-Path $PSScriptRoot "..\StremioCommunity\StremioCommunity_TemporaryKey.pfx"
$cerPath = Join-Path $PSScriptRoot "..\StremioCommunity\StremioCommunity_TemporaryKey.cer"

$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
    -FriendlyName "Stremio Community dev signing" -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cerPath | Out-Null
Write-Host "Created $pfxPath and $cerPath"
