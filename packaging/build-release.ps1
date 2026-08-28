$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

& (Join-Path $PSScriptRoot 'publish-x86.ps1')
if (-not (Test-Path (Join-Path $Root 'artifacts\certificate\MemoryBlackHole-dev.pfx'))) {
    & (Join-Path $PSScriptRoot 'create-self-signed-cert.ps1')
}
& (Join-Path $PSScriptRoot 'sign-release.ps1')

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $searchBases = @(
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramW6432,
        'C:\Program Files',
        'C:\Program Files (x86)'
    ) | Where-Object { $_ -and (Test-Path $_) } | Sort-Object -Unique

    $iscc = $searchBases | ForEach-Object {
        Get-ChildItem "$_\Inno Setup*" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    } | Where-Object { $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup, then rerun this script.' }

$installerDir = Join-Path $Root 'artifacts\installer'
New-Item $installerDir -ItemType Directory -Force | Out-Null
& $iscc (Join-Path $PSScriptRoot 'MemoryBlackHole.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Write-Host "Release package created under $installerDir"
