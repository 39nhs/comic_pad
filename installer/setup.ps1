# ComicPad 설치 파일 (ComicPad 설치파일.exe 안에서 실행됨)
# 1) 설치할 폴더 고르기  2) 프로그램 풀기 (작업물 data 폴더는 건드리지 않음)  3) 바로가기·앱 목록 등록 (installer\install.ps1)
param(
  [string]$Target,          # 시험용: 폴더를 묻지 않고 여기에 설치
  [switch]$NoRegister,      # 시험용: 바로가기·앱 목록 등록을 하지 않음
  [switch]$Quiet            # 시험용: 메시지 창 없이
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
[Windows.Forms.Application]::EnableVisualStyles()
$here = $PSScriptRoot
$zip = Join-Path $here 'payload.zip'

function Box($text, $buttons = 'OK', $icon = 'Information') {
  if ($Quiet) { return 'Yes' }
  return [Windows.Forms.MessageBox]::Show($text, 'ComicPad 설치', $buttons, $icon)
}

# 이미 설치되어 있으면 그 자리에 덮어써서 업데이트 (작업물은 그대로)
$reg = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ComicPad' -ErrorAction SilentlyContinue
$default = if ($reg -and $reg.InstallLocation -and (Test-Path $reg.InstallLocation)) { $reg.InstallLocation } else { Join-Path $env:LOCALAPPDATA 'Programs\ComicPad' }
$isUpdate = $reg -and $reg.InstallLocation -eq $default

if (-not $Target) {
  $msg = if ($isUpdate) { "이미 설치된 ComicPad 를 새 버전으로 바꿉니다.`n`n위치: $default`n(그린 그림·설정이 든 data 폴더는 그대로 둡니다)`n`n[예] 이 위치에 업데이트`n[아니요] 다른 폴더 고르기`n[취소] 그만두기" }
         else { "ComicPad 만화 그림판을 설치합니다.`n`n설치 위치: $default`n`n[예] 이 위치에 설치`n[아니요] 다른 폴더 고르기 (예: G 드라이브)`n[취소] 그만두기" }
  $r = Box $msg 'YesNoCancel' 'Question'
  if ($r -eq 'Cancel') { exit 0 }
  if ($r -eq 'No') {
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'ComicPad 를 설치할 폴더를 고르세요 (그 안에 ComicPad 폴더가 만들어집니다)'
    $dlg.ShowNewFolderButton = $true
    if ($dlg.ShowDialog() -ne 'OK') { exit 0 }
    $Target = $dlg.SelectedPath
    if ((Split-Path -Leaf $Target) -ne 'ComicPad' -and -not (Test-Path (Join-Path $Target 'server.py'))) { $Target = Join-Path $Target 'ComicPad' }
  } else { $Target = $default }
}

Write-Host ''
Write-Host "  ComicPad 를 설치하는 중: $Target" -ForegroundColor Yellow
New-Item -ItemType Directory -Force $Target | Out-Null

# 이 폴더에서 실행 중인 ComicPad (창·서버 python) 찾기: 이 폴더 안의 실행 파일이거나 이 폴더의 server.py 를 돌리는 것만
function Get-Running {
  $dir = [IO.Path]::GetFullPath($Target).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  $srv = $dir + 'server.py'
  @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessId -ne $PID -and (
      ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) -or
      ($_.CommandLine -and $_.CommandLine.IndexOf($srv, [StringComparison]::OrdinalIgnoreCase) -ge 0))
  })
}

# 켜져 있으면 파일이 잠겨 설치가 안 되므로 먼저 끈다 (창은 정상적으로 닫아서 저장 안 한 작업을 물어보게)
$procs = Get-Running
if ($procs.Count) {
  $r = Box "ComicPad 가 켜져 있습니다. 끄고 설치를 계속합니다.`n`n저장하지 않은 작업이 있으면 ComicPad 창이 저장할지 물어봅니다.`n`n[확인] 끄고 계속`n[취소] 그만두기" 'OKCancel' 'Warning'
  if ($r -eq 'Cancel') { exit 0 }
  Write-Host '  켜져 있는 ComicPad 를 끄는 중...' -ForegroundColor Yellow
  while ($true) {
    # 1) 창(ComicPad.exe 등 화면이 있는 것)은 닫기 요청
    foreach ($p in $procs) { $gp = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue; if ($gp -and $gp.MainWindowHandle -ne 0) { [void]$gp.CloseMainWindow() } }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 30 -and (@(Get-Running | Where-Object { $gp = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; $gp -and $gp.MainWindowHandle -ne 0 }).Count)) { Start-Sleep -Milliseconds 500 }
    $windows = @(Get-Running | Where-Object { $gp = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; $gp -and $gp.MainWindowHandle -ne 0 })
    if (-not $windows.Count) { break }
    $r = Box "ComicPad 창이 아직 열려 있습니다. 저장한 뒤 창을 닫고 [다시 시도] 를 누르세요." 'RetryCancel' 'Warning'
    if ($r -eq 'Cancel') { exit 0 }
    $procs = Get-Running
  }
  # 2) 남은 서버(python)는 이 폴더 것만 끄기 (저장은 바로바로 끝나 있음)
  # 창을 닫으면 창 프로그램이 서버도 끄므로 잠깐 기다린 뒤, 그래도 남은 것만 끈다
  for ($i = 0; $i -lt 16 -and (Get-Running).Count; $i++) { Start-Sleep -Milliseconds 500 }
  foreach ($p in Get-Running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep 1
}

# 프로그램 풀기 (data 폴더는 꾸러미에 없으므로 기존 작업물은 그대로)
while ($true) {
  try { Expand-Archive -Path $zip -DestinationPath $Target -Force; break }
  catch {
    $left = Get-Running
    $who = ($left | ForEach-Object { $_.Name + ' (' + $_.ProcessId + ')' }) -join ', '
    $r = Box ("파일을 바꾸지 못했습니다. 다른 프로그램이 ComicPad 파일을 쓰고 있을 수 있습니다.`n" + ($(if ($who) { "켜져 있는 것: $who`n" } else { '' })) + "`n$($_.Exception.Message)`n`n그 프로그램을 닫고 [다시 시도] 를 누르세요.") 'RetryCancel' 'Warning'
    if ($r -eq 'Cancel') { exit 1 }
    foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
  }
}
foreach ($d in 'projects', 'assets', 'autosave', 'fonts', 'exports', 'config', 'trash', 'versions') {
  New-Item -ItemType Directory -Force (Join-Path $Target "data\$d") | Out-Null
}
Write-Host '  파일을 풀었습니다.' -ForegroundColor Green

if (-not $NoRegister) {
  # 바로가기·앱 목록 등록, Python 확인 (없으면 설치 안내)
  $p = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Target 'installer\install.ps1')) -NoNewWindow -Wait -PassThru
  if ($p.ExitCode -ne 0) {
    Box "설치를 마치지 못했습니다 (Python 이 필요합니다).`n`nhttps://www.python.org 에서 Python 3 을 설치한 뒤`n$Target\ComicPad 설치.bat 을 실행하면 이어서 설치됩니다." 'OK' 'Warning' | Out-Null
    exit 1
  }
}

$extra = ''
$r = Box "설치가 끝났습니다.`n`n위치: $Target`n바탕화면과 시작 메뉴에 ComicPad 가 생겼습니다.$extra`n`n지금 설명서를 열까요?" 'YesNo' 'Information'
if ($r -eq 'Yes' -and -not $Quiet) { Start-Process (Join-Path $Target 'app\manual.html') }
exit 0
