$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Exe = Join-Path $Root 'artifacts\publish\win-x86\MemoryBlackHole.exe'
$Pfx = Join-Path $Root 'artifacts\certificate\MemoryBlackHole-dev.pfx'
if (-not (Test-Path $Exe)) { throw "EXE not found. Run publish-x86.ps1 first: $Exe" }
if (-not (Test-Path $Pfx)) { throw "Certificate not found. Run create-self-signed-cert.ps1 first: $Pfx" }

$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
if (-not $signtool) {
    $kits = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $signtool = Get-ChildItem $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK or Visual Studio Build Tools.' }

$secure = Read-Host 'Enter the certificate password' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

Write-Host "Signing $Exe"
& $signtool sign /fd SHA256 /f $Pfx /p $password /tr http://timestamp.digicert.com /td SHA256 $Exe
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'Timestamp server failed; retrying without timestamp.'
    & $signtool sign /fd SHA256 /f $Pfx /p $password $Exe
}
if ($LASTEXITCODE -ne 0) { throw 'Code signing failed.' }
& $signtool verify /pa /all $Exe
$verifyExitCode = $LASTEXITCODE
$auth = Get-AuthenticodeSignature -FilePath $Exe

if ($auth.Status -eq 'Valid') {
    Write-Host 'Signature verified and trusted.'
}
elseif ($auth.Status -eq 'Unknown' -and $null -ne $auth.SignerCertificate) {
    Write-Warning 'Signature is present, but the self-signed certificate is not trusted by this Windows installation.'
    Write-Host "Signer: $($auth.SignerCertificate.Subject)"
    Write-Host 'This is expected for a development certificate. The EXE is signed; Windows may display Unknown Publisher.'
}
else {
    throw "Signature verification failed. Authenticode status: $($auth.Status); signtool exit code: $verifyExitCode"
}
