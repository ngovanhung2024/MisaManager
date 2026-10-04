<#
.SYNOPSIS
  Trusts the internal MisaManager signing certificate for the current Windows user,
  so the signed MSI/app shows a verified publisher instead of "Unknown publisher".
#>
param([Parameter(Mandatory = $true)][string]$Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$full = [IO.Path]::GetFullPath($Path)
Import-Certificate -FilePath $full -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
Import-Certificate -FilePath $full -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
Write-Output "Đã tin cậy chứng chỉ ký số: $full"
