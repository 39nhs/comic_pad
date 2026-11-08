# ComicPad 제거: 바로가기와 "설치된 앱" 등록만 지운다. 프로그램 폴더와 작업물(data)은 그대로 둔다.
$ErrorActionPreference = 'SilentlyContinue'
$Root = Split-Path -Parent $PSScriptRoot

Write-Host ''
Write-Host '  ComicPad 제거' -ForegroundColor Yellow
Write-Host "  작업물(data 폴더)과 프로그램 파일은 지우지 않습니다: $Root"
Write-Host ''
$ans = Read-Host '  바로가기와 앱 목록 등록을 지울까요? (Y/N)'
if ($ans -notmatch '^[Yy]') { Write-Host '  취소했습니다.'; Start-Sleep 1; exit 0 }

# 이 폴더에서 실행 중인 ComicPad 끄기 (창은 닫기 요청, 이 폴더의 서버 python 만 종료)
$dir = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$mine = { @(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($dir + 'server.py', [StringComparison]::OrdinalIgnoreCase) -ge 0)) }) }
foreach ($p in (& $mine)) { $gp = Get-Process -Id $p.ProcessId; if ($gp -and $gp.MainWindowHandle -ne 0) { [void]$gp.CloseMainWindow() } }
for ($i = 0; $i -lt 60 -and @(& $mine | Where-Object { (Get-Process -Id $_.ProcessId).MainWindowHandle -ne 0 }).Count; $i++) { Start-Sleep -Milliseconds 500 }
foreach ($p in (& $mine)) { Stop-Process -Id $p.ProcessId -Force }

Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ComicPad.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'ComicPad') -Recurse -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ComicPad' -Recurse -Force

Write-Host '  제거했습니다. 프로그램 폴더를 완전히 지우려면 직접 삭제하세요 (작업물이 들어 있으니 먼저 백업!).' -ForegroundColor Green
Start-Sleep 3
