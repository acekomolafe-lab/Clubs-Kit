[CmdletBinding()]
param(
  [string]$RepoRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$ErrorActionPreference = 'Stop'
$repo = Join-Path $RepoRoot 'FMSuperScout-1.5.0'
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist 'stage'
$installer = Join-Path $repo 'installer'

Write-Host "Starting FMSuperScout Installer Build..." -ForegroundColor Cyan

# 1. Generate icon and wizard BMPs
Write-Host "Generating icon and wizard bitmaps..."
node (Join-Path $installer 'make-icon.js')
node (Join-Path $installer 'make-wizard-images.js')

# 2. Re-create clean stage directories
if (Test-Path $stage) {
  Remove-Item $stage -Recurse -Force
}
$viewer = Join-Path $stage 'viewer'
$bepx = Join-Path $stage 'bepinex'
New-Item -ItemType Directory -Force -Path (Join-Path $viewer 'app') | Out-Null
New-Item -ItemType Directory -Force -Path $bepx | Out-Null

# 3. Extract BepInEx payload if zip exists
$bepxZip = Join-Path $installer 'bepinex-payload.zip'
if (Test-Path $bepxZip) {
  Write-Host "Extracting BepInEx payload..."
  Expand-Archive -Path $bepxZip -DestinationPath $bepx -Force
} else {
  Write-Warning "BepInEx payload zip not found at $bepxZip"
}

# 4. Copy viewer application files
Write-Host "Packaging viewer..."
$builtExe = Join-Path $repo 'src-tauri\target\release\FMSuperScout.exe'
if (-not (Test-Path $builtExe)) {
  $builtExe = Join-Path $repo 'FMSuperScout.exe'
}
if (Test-Path $builtExe) {
  Copy-Item $builtExe -Destination (Join-Path $viewer 'FMSuperScout.exe') -Force
} else {
  throw "FMSuperScout.exe not found! Build it first with cargo build --release."
}

# Copy web frontend files
Copy-Item (Join-Path $repo 'app\*') -Destination (Join-Path $viewer 'app') -Recurse -Force

# Copy legacy runner, icon, and node runtime
Copy-Item (Join-Path $installer 'FMSuperScout.vbs') -Destination $viewer -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $installer 'icon.ico') -Destination $viewer -Force
$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
if ($nodeCmd) {
  Copy-Item $nodeCmd.Source -Destination (Join-Path $viewer 'node.exe') -Force
}

# 5. Copy plugin and license
$pluginDll = Join-Path $repo 'plugin\dist\FMSuperScout.dll'
if (-not (Test-Path $pluginDll)) {
  $pluginDll = Join-Path $repo 'plugin\bin\Release\FMSuperScout.dll'
}
if (Test-Path $pluginDll) {
  Copy-Item $pluginDll -Destination (Join-Path $stage 'FMSuperScout.dll') -Force
  Write-Host "Plugin DLL staged: $pluginDll"
} else {
  throw "Plugin DLL not found at $pluginDll"
}

$licenseFile = Join-Path $installer 'LICENSE-BepInEx.txt'
if (Test-Path $licenseFile) {
  Copy-Item $licenseFile -Destination $stage -Force
}

# 6. Locate Inno Setup Compiler (ISCC)
$isccPaths = @(
  (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccPaths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
  throw "Inno Setup compiler (ISCC.exe) not found. Please install Inno Setup 6."
}

Write-Host "Compiling installer using: $iscc"
$issFile = Join-Path $installer 'FMSuperScout.iss'
& $iscc $issFile
if ($LASTEXITCODE -ne 0) {
  throw "ISCC failed with exit code $LASTEXITCODE"
}

$setupExe = Join-Path $dist 'FMSuperScout-Setup.exe'
if (-not (Test-Path $setupExe)) {
  throw "Setup executable not found at $setupExe"
}

# 7. Checksums and version metadata
$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()
Set-Content -Path "$setupExe.sha256" -Value "$hash  FMSuperScout-Setup.exe" -NoNewline

$ver = (Select-String -Path $issFile -Pattern 'MyAppVersion "([^"]+)"').Matches[0].Groups[1].Value
Set-Content -Path (Join-Path $dist 'version.json') -Value "{`"tag`":`"v$ver`"}" -NoNewline -Encoding ascii

$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 2)
Write-Host "Installer successfully built: $setupExe ($sizeMb MB)" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
