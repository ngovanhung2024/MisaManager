<#
.SYNOPSIS
  Creates a self-signed code-signing certificate for internal releases.

.DESCRIPTION
  Writes <OutputDirectory>\MisaManager-signing.pfx (private key, upload to GitHub secret
  CODE_SIGNING_PFX_BASE64 as base64 with password CODE_SIGNING_PASSWORD) and
  MisaManager-signing.cer (public key to trust on every PC that installs the MSI).
  A self-signed certificate does NOT give a SmartScreen reputation; buy an OV/EV
  certificate or use Azure Trusted Signing for machines you do not manage.

.EXAMPLE
  .\scripts\new-signing-certificate.ps1 -OutputDirectory $env:USERPROFILE\Desktop\signing
#>
param(
    [string]$OutputDirectory = (Join-Path $env:USERPROFILE 'MisaManager-signing'),
    [string]$Subject = 'CN=MisaManager Internal Code Signing',
    [int]$Years = 5
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$password = Read-Host -AsSecureString 'Mật khẩu cho file PFX'
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears($Years) -CertStoreLocation Cert:\CurrentUser\My
$pfx = Join-Path $OutputDirectory 'MisaManager-signing.pfx'
$cer = Join-Path $OutputDirectory 'MisaManager-signing.cer'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $cert.Thumbprint)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'pfx-base64.txt'), [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
Write-Output "Đã tạo: $pfx"
Write-Output "Public key: $cer"
Write-Output "Nội dung secret CODE_SIGNING_PFX_BASE64: $(Join-Path $OutputDirectory 'pfx-base64.txt') (xóa file này sau khi dán vào GitHub)."
Write-Output "Trên mỗi máy cài MSI: .\scripts\trust-signing-certificate.ps1 -Path `"$cer`""
