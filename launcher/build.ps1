# ComicPad.exe (전용 창) 만들기: Windows 에 들어 있는 .NET Framework 컴파일러(csc.exe)로
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path $here -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$lib = Join-Path $here 'lib\net462'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
  "/out:$root\ComicPad.exe" "/win32icon:$root\ComicPad.ico" "/win32manifest:$here\app.manifest" `
  "/r:$lib\Microsoft.Web.WebView2.Core.dll" "/r:$lib\Microsoft.Web.WebView2.WinForms.dll" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  "$here\ComicPad.cs" "$here\NativePen.cs" "$here\Wintab.cs"
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패" }
# 실행에 필요한 WebView2 파일 (마이크로소프트 서명 파일 그대로)
# (ComicPad 창이 열려 있으면 DLL 이 잠겨 덮어쓰지 못함 — 같은 파일이니 그대로 둬도 됨)
Copy-Item "$lib\Microsoft.Web.WebView2.Core.dll", "$lib\Microsoft.Web.WebView2.WinForms.dll" $root -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $here 'lib\runtimes') $root -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "만들었습니다: $root\ComicPad.exe"
