$ErrorActionPreference = 'Stop'
$CertDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\certificate'))
New-Item $CertDir -ItemType Directory -Force | Out-Null
$PfxPath = Join-Path $CertDir 'MemoryBlackHole-dev.pfx'
$CerPath = Join-Path $CertDir 'MemoryBlackHole-dev.cer'

$secure = Read-Host 'Enter a password for the local development certificate' -AsSecureString
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject 'CN=MemoryBlackHole Development, O=vicz' `
  -FriendlyName 'MemoryBlackHole Development Code Signing' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -HashAlgorithm SHA256 `
  -NotAfter (Get-Date).AddYears(3)

Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $CerPath | Out-Null
Write-Host "Certificate created: $PfxPath"
Write-Host "Public certificate: $CerPath"
Write-Host 'This is a development certificate. Windows may still show Unknown Publisher until the certificate is trusted locally.'
