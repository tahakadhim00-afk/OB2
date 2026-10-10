# Builds the portable release (PRD §12.2, §12.4):
#   publish/OB2-portable/   OB2.exe (single-file, self-contained), host.exe (when present),
#                           portable.flag, README.txt
#   publish/OB2-portable.zip
#   publish/SHA256SUMS.txt
# Usage (repo root):  powershell -ExecutionPolicy Bypass -File build/publish.ps1 [-Version 1.0.0]
param([string]$Version = "0.5.0", [switch]$IncludeHost)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish"
$stage = Join-Path $out "OB2-portable"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $stage | Out-Null

function Publish([string]$project, [string]$target, [bool]$compress) {
    $publishArgs = @("publish", (Join-Path $root $project), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=$($compress.ToString().ToLower())", "-p:DebugType=none",
        "-p:Version=$Version", "-o", $target, "-nologo", "-v", "q")
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

# Not compressed (decision D-56): a compressed single-file exe unpacks its assemblies into private
# memory (~98 MB with 10 downloads vs ~36 MB uncompressed, AC-12). The zip compresses it anyway.
Publish "src/OB2.App/OB2.App.csproj" (Join-Path $out "app") $false
Copy-Item (Join-Path $out "app/OB2.exe") $stage

$hostProject = Join-Path $root "src/OB2.Host/OB2.Host.csproj"
if ($IncludeHost -and (Test-Path $hostProject)) {
    # The host starts on every captured link, so it is not compressed (no decompression at launch).
    Publish "src/OB2.Host/OB2.Host.csproj" (Join-Path $out "host") $false
    Copy-Item (Join-Path $out "host/host.exe") $stage
}

if (Test-Path (Join-Path $root "extension/dist")) {
    Copy-Item -Recurse (Join-Path $root "extension/dist") (Join-Path $stage "chrome-extension")
}

Set-Content -Path (Join-Path $stage "portable.flag") -Value "Settings, history, and logs are stored next to OB2.exe while this file exists." -Encoding ascii
Copy-Item (Join-Path $root "build/README.txt") (Join-Path $stage "README.txt")

$zip = Join-Path $out "OB2-portable.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$sums = @(Get-ChildItem $stage -File -Recurse | Where-Object { $_.Extension -eq ".exe" } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
})
$sums += "{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(), "OB2-portable.zip"
$sums | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ascii

Get-ChildItem $stage -Recurse -File | ForEach-Object { "{0,-28} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"{0,-28} {1,8:N1} MB" -f "OB2-portable.zip", ((Get-Item $zip).Length / 1MB)
