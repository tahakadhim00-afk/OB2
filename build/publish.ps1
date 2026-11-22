# Builds a portable release of OB2 (PRD §12.2, §12.4) into release/. Never into publish/: that is
# where a copy in daily use may live, with its settings, history and video tools (D-84).
#   release/OB2-portable/        OB2.exe, host.exe, chrome-extension/, portable.flag, README.txt, LICENSE.txt
#   release/OB2-portable.zip     what people download, and what OB2's updater installs (D-85)
#   release/SHA256SUMS.txt       sha256sum lines for both programs and the zip
#   release/SHA256SUMS.txt.sig   with -Sign or -GitHub: the developer's signature, which the updater checks
#
# Usage (repo root):
#   powershell -ExecutionPolicy Bypass -File build/publish.ps1                        build
#   powershell -ExecutionPolicy Bypass -File build/publish.ps1 -Sign                  build and sign
#   powershell -ExecutionPolicy Bypass -File build/publish.ps1 -GitHub -Notes notes.md [-Prerelease]
#       Build, sign, and publish GitHub release v<version> at the current (pushed) commit. Every
#       copy of OB2 sees it at its next update check. With -Prerelease nobody does (the updater
#       reads only the latest full release) until it is promoted on GitHub, so it can be tried first.
#
# The version is the one in Directory.Build.props, which also goes into the app: the release tag
# and the app must agree, or an updated copy would keep offering the same update.
# -KeyFile defaults to %USERPROFILE%\OB2-signing\ob2-release-key.pem; make one with
#   dotnet run --project build/OB2.ReleaseSigner -- keygen --out "%USERPROFILE%\OB2-signing\ob2-release-key.pem"
param(
    [switch]$Sign,
    [switch]$GitHub,
    [string]$Notes,
    [switch]$Prerelease,
    [string]$KeyFile = (Join-Path $env:USERPROFILE "OB2-signing\ob2-release-key.pem"),
    # Local test builds only: sign with a key OB2 does not trust. Refused with -GitHub.
    [switch]$AllowUntrustedKey,
    # Kept so older commands still run: host.exe and the extension are always in a release now.
    [switch]$IncludeHost,
    # Must match Directory.Build.props if given.
    [string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "release"
$stage = Join-Path $out "OB2-portable"
$work = Join-Path $out "build"

# Runs a native command for its exit code only. Windows PowerShell turns a native program's
# stderr into a terminating error under "Stop", so the preference is relaxed around it.
function Invoke-ExitCode([scriptblock]$command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $command 2>&1 | Out-Null; return $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
}

$propsVersion = ([xml](Get-Content -Raw (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($Version -and $Version -ne $propsVersion) {
    throw "-Version $Version differs from Directory.Build.props ($propsVersion). Change it there, and in extension/manifest.json and package.json, so the app, the extension and the release agree."
}
$Version = $propsVersion
$tag = "v$Version"

# Everything that can stop a GitHub release is checked before the build, not after it.
if ($GitHub) {
    if ($AllowUntrustedKey) { throw "-AllowUntrustedKey is for local test builds. A GitHub release must be signed with a key OB2 trusts." }
    if (-not $Notes -or -not (Test-Path -LiteralPath $Notes)) { throw "Write the release notes in a file and pass it with -Notes: OB2 shows them as ""What's new""." }
    $Notes = (Resolve-Path -LiteralPath $Notes).Path
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "-GitHub needs the GitHub CLI: winget install GitHub.cli, then gh auth login." }
    if ((Invoke-ExitCode { gh release view $tag }) -eq 0) { throw "GitHub already has a release $tag. Raise the version in Directory.Build.props first." }
    if (git -C $root status --porcelain) { throw "Commit your changes first: the release is built from the current commit and tagged there." }
    $commit = (git -C $root rev-parse HEAD).Trim()
    if (-not (git -C $root branch -r --contains $commit)) { throw "Push the current commit first: GitHub tags the release at $commit." }
    $Sign = $true
}
if ($Sign -and -not (Test-Path -LiteralPath $KeyFile)) {
    throw "No release key at $KeyFile. Make one with: dotnet run --project build/OB2.ReleaseSigner -- keygen --out ""$KeyFile"""
}

# A copy of OB2 running from release/ holds files open; deleting around them would leave a half-empty folder.
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith("$out\", [StringComparison]::OrdinalIgnoreCase) })
if ($running) { throw "Close OB2 first: $(($running | ForEach-Object { $_.Path }) -join ', ') is running from the release folder." }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

function Publish([string]$project, [string]$target) {
    # Not compressed (decision D-56): a compressed single-file exe unpacks its assemblies into
    # private memory (~98 MB with 10 downloads vs ~36 MB uncompressed, AC-12). The zip compresses
    # it anyway. host.exe starts on every captured link, so it is not compressed either.
    $publishArgs = @("publish", (Join-Path $root $project), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=false", "-p:DebugType=none",
        "-o", $target, "-nologo", "-v", "q")
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

Publish "src/OB2.App/OB2.App.csproj" (Join-Path $work "app")
Copy-Item (Join-Path $work "app/OB2.exe") $stage
Publish "src/OB2.Host/OB2.Host.csproj" (Join-Path $work "host")
Copy-Item (Join-Path $work "host/host.exe") $stage

# The unpacked Chrome/Edge extension (§12.5), built from source so a release never carries a stale copy.
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw "Node.js and npm are needed to build the extension." }
$extension = Join-Path $root "extension"
Push-Location $extension
try {
    & npm ci --no-fund --no-audit --loglevel=error
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    & npm run --silent build
    if ($LASTEXITCODE -ne 0) { throw "extension build failed" }
}
finally { Pop-Location }
Copy-Item -Recurse (Join-Path $extension "dist") (Join-Path $stage "chrome-extension")

Set-Content -Path (Join-Path $stage "portable.flag") -Value "Settings, history, and logs are stored next to OB2.exe while this file exists." -Encoding ascii
Copy-Item (Join-Path $root "build/README.txt") (Join-Path $stage "README.txt")
Copy-Item (Join-Path $root "LICENSE") (Join-Path $stage "LICENSE.txt")
Remove-Item -LiteralPath $work -Recurse -Force

$zip = Join-Path $out "OB2-portable.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$sums = Join-Path $out "SHA256SUMS.txt"
$lines = @(Get-ChildItem $stage -File -Recurse | Where-Object { $_.Extension -eq ".exe" } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
})
$lines += "{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(), "OB2-portable.zip"
$lines | Set-Content $sums -Encoding ascii

if ($Sign) {
    # D-84: the signer asks for the key's password, signs, and checks the signature the way OB2 will.
    $signArgs = @("run", "--project", (Join-Path $root "build/OB2.ReleaseSigner"), "-c", "Release", "-v", "q", "--",
        "sign", "--key", $KeyFile, "--in", $sums, "--out", "$sums.sig")
    if ($AllowUntrustedKey) { $signArgs += "--allow-untrusted" }
    & dotnet @signArgs
    if ($LASTEXITCODE -ne 0) { throw "Signing failed; nothing was published." }
}

Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -eq ".exe" } | ForEach-Object { "{0,-28} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"{0,-28} {1,8:N1} MB" -f "OB2-portable.zip", ((Get-Item $zip).Length / 1MB)
"Version $Version in $out"

if ($GitHub) {
    $assets = @($zip, $sums, "$sums.sig")
    $ghArgs = @("release", "create", $tag) + $assets + @("--title", "OB2 $Version", "--notes-file", $Notes, "--target", $commit)
    $ghArgs += $(if ($Prerelease) { "--prerelease" } else { "--latest" })
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed. The build is in $out; fix the problem and run gh release create by hand, or run this again." }
    if ($Prerelease) { "Published ${tag} as a pre-release: copies of OB2 will not offer it until it is marked as the latest release on GitHub." }
    else { "Published ${tag}: copies of OB2 offer it at their next update check." }
}
