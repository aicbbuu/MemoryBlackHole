$ErrorActionPreference = 'Stop'
$ProjectDir = Join-Path $PSScriptRoot '..\src\MemoryBlackHole'
$ProjectDir = [IO.Path]::GetFullPath($ProjectDir)
$PublishDir = Join-Path $PSScriptRoot '..\artifacts\publish\win-x64'
$PublishDir = [IO.Path]::GetFullPath($PublishDir)

Write-Host 'Restoring dependencies...'
dotnet restore $ProjectDir
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item $PublishDir -ItemType Directory -Force | Out-Null

Write-Host 'Publishing self-contained Windows x64 single-file EXE...'
dotnet publish (Join-Path $ProjectDir 'MemoryBlackHole.csproj') `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $PublishDir

$Exe = Join-Path $PublishDir 'MemoryBlackHole.exe'
if (-not (Test-Path $Exe)) { throw "Publish failed: $Exe was not created." }
Write-Host "Created: $Exe"
Write-Host 'Next: run create-self-signed-cert.ps1, then sign-release.ps1.'
