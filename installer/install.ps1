# ComicPad 설치: 바탕화면/시작 메뉴 바로가기 + Windows "설치된 앱" 목록 등록 (관리자 권한 필요 없음)
# 프로그램과 작업물(data)은 지금 폴더에 그대로 둔다.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Server = Join-Path $Root 'server.py'
$Icon = Join-Path $Root 'ComicPad.ico'
$Version = (Get-Content (Join-Path $Root 'VERSION') -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $Version) { $Version = '1.1.0' }

function Say($msg, $color = 'Gray') { Write-Host $msg -ForegroundColor $color }

Say ''
Say '  ComicPad 설치' 'Yellow'
Say "  위치: $Root"
Say ''

if (-not (Test-Path $Server)) { Say '  server.py 를 찾을 수 없습니다. ComicPad 폴더 안의 installer 폴더에서 실행해 주세요.' 'Red'; exit 1 }

# ---- Python (콘솔 창 없이 실행되는 pythonw) 찾기 ----
function Find-Pythonw {
  # 설치 파일에 들어 있는 ComicPad 전용 Python 이 먼저 (따로 설치할 필요 없음)
  $own = Join-Path $Root 'python\pythonw.exe'
  if (Test-Path $own) { return $own }
  foreach ($name in 'pythonw.exe', 'pyw.exe') {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c -and $c.Source -notlike '*WindowsApps*') { return $c.Source }
  }
  $cands = @(
    "$env:LOCALAPPDATA\Programs\Python\Python313\pythonw.exe",
    "$env:LOCALAPPDATA\Programs\Python\Python312\pythonw.exe",
    "$env:LOCALAPPDATA\Programs\Python\Python311\pythonw.exe",
    "$env:ProgramFiles\Python313\pythonw.exe",
    "$env:ProgramFiles\Python312\pythonw.exe",
    "$env:ProgramFiles\Python311\pythonw.exe",
    "$env:LOCALAPPDATA\Programs\Python\Launcher\pyw.exe"
  )
  foreach ($p in $cands) { if (Test-Path $p) { return $p } }
  return $null
}

$Pyw = Find-Pythonw
if (-not $Pyw) {
  Say '  Python 이 설치되어 있지 않습니다. ComicPad 를 실행하려면 Python 3 이 필요합니다.' 'Yellow'
  $ans = Read-Host '  지금 winget 으로 Python 3.12 를 설치할까요? (Y/N)'
  if ($ans -match '^[Yy]') {
    winget install --id Python.Python.3.12 -e --scope user --accept-source-agreements --accept-package-agreements
    $Pyw = Find-Pythonw
  }
  if (-not $Pyw) { Say '  Python 을 찾지 못해 설치를 멈춥니다. https://www.python.org 에서 설치 후 다시 실행하세요.' 'Red'; exit 1 }
}
Say "  Python: $Pyw" 'Green'

# ---- WebView2 (ComicPad 전용 창): Windows 11 에는 들어 있음. 없으면 함께 넣은 마이크로소프트 설치기로 ----
function Test-WebView2 {
  $id = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
  foreach ($k in "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$id", "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id", "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$id") {
    $v = (Get-ItemProperty $k -ErrorAction SilentlyContinue).pv
    if ($v -and $v -ne '0.0.0.0') { return $v }
  }
  return $null
}
$wv = Test-WebView2
if ($wv) { Say "  WebView2: $wv (전용 창 사용)" 'Green' }
else {
  # 1) 함께 넣은 전체 설치본 (인터넷 없이)  2) 안 되면 작은 설치기 (인터넷 필요)
  $full = Join-Path $Root 'redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
  $small = Join-Path $Root 'redist\MicrosoftEdgeWebview2Setup.exe'
  $arm = ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') -or ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') -or `
         (@(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq 12 }).Count -gt 0)
  # 함께 넣은 전체 설치본은 x64 용. ARM PC 는 아래 작은 설치기가 ARM 용을 받아 설치 (Windows 11 ARM 에는 보통 이미 들어 있음)
  if ((Test-Path $full) -and [Environment]::Is64BitOperatingSystem -and -not $arm) {
    Say '  WebView2 가 없어 설치합니다 (함께 넣은 마이크로소프트 전체 설치본, 인터넷 필요 없음)...' 'Yellow'
    try { Start-Process $full -ArgumentList '/silent', '/install' -Wait } catch { }
  }
  if (-not (Test-WebView2) -and (Test-Path $small)) {
    Say '  마이크로소프트 설치기로 다시 시도합니다 (인터넷 필요)...' 'Yellow'
    try { Start-Process $small -ArgumentList '/silent', '/install' -Wait } catch { }
  }
  $wv = Test-WebView2
  if ($wv) { Say "  WebView2: $wv 설치됨" 'Green' }
  else { Say '  WebView2 를 설치하지 못했습니다. ComicPad 는 Edge 앱 창으로 열립니다. (redist 폴더의 MicrosoftEdgeWebView2RuntimeInstallerX64.exe 를 직접 실행해도 됩니다)' 'Yellow' }
}

# Edge 확인 (없으면 기본 브라우저로 열림)
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) { Say "  Edge: $edge" 'Green' } else { Say '  Edge 를 찾지 못했습니다. 기본 브라우저로 열립니다.' 'Yellow' }

# 작업 폴더
foreach ($d in 'projects', 'assets', 'autosave', 'fonts', 'exports', 'config', 'trash') {
  New-Item -ItemType Directory -Force (Join-Path $Root "data\$d") | Out-Null
}

# ---- 바로가기 ----
$shell = New-Object -ComObject WScript.Shell
function Make-Shortcut($path, $target, $arguments, $desc, $icon) {
  $lnk = $shell.CreateShortcut($path)
  $lnk.TargetPath = $target
  $lnk.Arguments = $arguments
  $lnk.WorkingDirectory = $Root
  $lnk.Description = $desc
  if ($icon) { $lnk.IconLocation = "$icon,0" }
  $lnk.Save()
}

$desktop = [Environment]::GetFolderPath('Desktop')
$startDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'ComicPad'
New-Item -ItemType Directory -Force $startDir | Out-Null

# 전용 창(ComicPad.exe)이 쓸 Python 위치를 적어 둠
Set-Content -Path (Join-Path $Root 'python_path.txt') -Value $Pyw -Encoding UTF8
$appExe = Join-Path $Root 'ComicPad.exe'
if (Test-Path $appExe) {
  # 전용 창: 서버를 켜고 ComicPad 창(WebView2)으로 엶 — Edge 창이 아님
  Make-Shortcut (Join-Path $desktop 'ComicPad.lnk') $appExe '' 'ComicPad 만화 그림판' $Icon
  Make-Shortcut (Join-Path $startDir 'ComicPad.lnk') $appExe '' 'ComicPad 만화 그림판' $Icon
} else {
  $args1 = '"' + $Server + '"'
  Make-Shortcut (Join-Path $desktop 'ComicPad.lnk') $Pyw $args1 'ComicPad 만화 그림판' $Icon
  Make-Shortcut (Join-Path $startDir 'ComicPad.lnk') $Pyw $args1 'ComicPad 만화 그림판' $Icon
}
$uninst = Join-Path $PSScriptRoot 'uninstall.ps1'
$psArgs = '-NoProfile -ExecutionPolicy Bypass -File "' + $uninst + '"'
Make-Shortcut (Join-Path $startDir 'ComicPad 제거.lnk') "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" $psArgs 'ComicPad 바로가기 제거' $Icon
$manual = Join-Path $Root 'app\manual.html'
if (Test-Path $manual) { Make-Shortcut (Join-Path $startDir 'ComicPad 설명서.lnk') $manual '' 'ComicPad 설명서 (전체 기능)' $Icon }
Make-Shortcut (Join-Path $startDir 'ComicPad 작업 폴더.lnk') "$env:SystemRoot\explorer.exe" ('"' + (Join-Path $Root 'data') + '"') 'ComicPad 작업물 폴더' $Icon
Say "  바탕화면 바로가기: $(Join-Path $desktop 'ComicPad.lnk')" 'Green'
Say "  시작 메뉴: $startDir" 'Green'

# ---- Windows 설정 > 앱 > 설치된 앱 목록에 등록 (현재 사용자만) ----
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ComicPad'
New-Item -Path $key -Force | Out-Null
$size = [int]((Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike "*\data\*" } | Measure-Object Length -Sum).Sum / 1KB)
$props = @{
  DisplayName = 'ComicPad'; DisplayVersion = $Version; Publisher = 'ComicPad'
  DisplayIcon = $Icon; InstallLocation = $Root; EstimatedSize = $size
  UninstallString = "`"$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`" $psArgs"
  NoModify = 1; NoRepair = 1
}
foreach ($k in $props.Keys) {
  $type = if ($props[$k] -is [int]) { 'DWord' } else { 'String' }
  New-ItemProperty -Path $key -Name $k -Value $props[$k] -PropertyType $type -Force | Out-Null
}
Say '  설치된 앱 목록에 등록했습니다.' 'Green'
Say ''
Say '  설치 완료! 바탕화면의 ComicPad 아이콘으로 실행하세요.' 'Yellow'
Say ''
