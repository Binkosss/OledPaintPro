param([Parameter(Mandatory)][string]$ExePath,[Parameter(Mandatory)][string]$PfxPath,[Parameter(Mandatory)][string]$Password)
if (-not (Test-Path $ExePath)) { Write-Warning "Plik nie istnieje: $ExePath"; exit 1 }
if (-not (Test-Path $PfxPath))  { Write-Warning "Cert nie znaleziony: $PfxPath"; exit 0 }
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $Password)
$result = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256
if ($result.Status -eq "Valid" -or $result.Status -eq "UnknownError") { Write-Host "[SIGN] OK - $ExePath ($($cert.Thumbprint))"; exit 0 }
else { Write-Warning "[SIGN] Status: $($result.Status)"; exit 0 }
