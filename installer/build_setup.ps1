# ComicPad 설치 파일 만들기: dist\ComicPad 설치파일.exe (Windows 기본 도구 IExpress 사용, 따로 받을 것 없음)
# 사용: powershell -ExecutionPolicy Bypass -File installer\build_setup.ps1
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $Root 'dist'
$stage = Join-Path $env:TEMP 'comicpad_build'
$pkg = Join-Path $stage 'pkg'

Write-Host '  ComicPad 설치 파일 만들기' -ForegroundColor Yellow
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $pkg, $dist | Out-Null

# 1) 꾸러미에 넣을 것: 프로그램만 (작업물 data, 만든 결과 dist 는 빼고)
foreach ($item in 'app', 'plugins', 'tools', 'models_bundled', 'runtimes', 'launcher') { Copy-Item (Join-Path $Root $item) $pkg -Recurse }
New-Item -ItemType Directory -Force (Join-Path $pkg 'installer') | Out-Null
foreach ($f in 'install.ps1', 'uninstall.ps1') { Copy-Item (Join-Path $PSScriptRoot $f) (Join-Path $pkg 'installer') }
foreach ($f in 'server.py', 'ComicPad.ico', 'README.md', 'LICENSE', 'NOTICE', 'VERSION', 'release_pubkey.json', 'ComicPad.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll') { Copy-Item (Join-Path $Root $f) $pkg }
Get-ChildItem $Root -Filter '*.bat' | Copy-Item -Destination $pkg
# 함께 설치할 프로그램: Python (내장형, 따로 설치하지 않고 ComicPad 폴더 안에서만 씀) + WebView2 설치기 (없는 PC 만)
$redist = Join-Path $Root 'redist'
$pyZip = Get-ChildItem $redist -Filter 'python-*-embed-amd64.zip' | Select-Object -First 1
if ($pyZip) { Expand-Archive $pyZip.FullName (Join-Path $pkg 'python') -Force; Write-Host "  Python 넣음: $($pyZip.Name)" -ForegroundColor Green }
else { Write-Host '  redist 에 Python 내장형이 없어 넣지 않았습니다 (설치할 PC 에 Python 필요)' -ForegroundColor Yellow }
New-Item -ItemType Directory -Force (Join-Path $pkg 'redist') | Out-Null
foreach ($n in 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe', 'MicrosoftEdgeWebview2Setup.exe') {
  $wv = Join-Path $redist $n
  if (Test-Path $wv) { Copy-Item $wv (Join-Path $pkg 'redist'); Write-Host "  WebView2 넣음: $n" -ForegroundColor Green }
}
Copy-Item (Join-Path $Root 'app\manual.html') (Join-Path $pkg 'ComicPad 설명서.html')

# 앱 파일 목록 갱신 (서버 없이도 열리도록 미리 받아 둘 목록)
$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if ($py) { & $py (Join-Path $Root 'server.py') --precache-only; Copy-Item (Join-Path $Root 'app\precache.json') (Join-Path $pkg 'app') -Force }

Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath (Join-Path $stage 'payload.zip') -CompressionLevel Optimal
Copy-Item (Join-Path $PSScriptRoot 'setup.ps1') $stage
Set-Content -Path (Join-Path $stage 'setup.cmd') -Encoding ASCII -Value "@echo off`r`nchcp 65001 >nul`r`npowershell -NoProfile -ExecutionPolicy Bypass -File ""%~dp0setup.ps1""`r`nif errorlevel 1 pause"

# 2) IExpress 로 한 파일 실행 파일 만들기
$tmpExe = Join-Path $stage 'ComicPadPackage.exe'
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$tmpExe
FriendlyName=ComicPad
AppLaunched=cmd /c setup.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
FILE0="payload.zip"
FILE1="setup.ps1"
FILE2="setup.cmd"
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@
$sedPath = Join-Path $stage 'setup.sed'
Set-Content -Path $sedPath -Value $sed -Encoding Default
$p = Start-Process -FilePath "$env:SystemRoot\System32\iexpress.exe" -ArgumentList @('/N', '/Q', $sedPath) -Wait -PassThru
if (-not (Test-Path $tmpExe)) { throw "IExpress 가 설치 파일을 만들지 못했습니다 (종료 코드 $($p.ExitCode))" }

# 이름에 setup/install 이 들어가면 Windows 가 관리자 권한을 요구하므로 한글 이름으로
$final = Join-Path $dist 'ComicPad 설치파일.exe'
Move-Item $tmpExe $final -Force
$mb = [math]::Round((Get-Item $final).Length / 1MB, 1)
Write-Host "  만들었습니다: $final ($mb MB)" -ForegroundColor Green
