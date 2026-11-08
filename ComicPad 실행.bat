@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "%~dp0ComicPad.exe" (
  start "" "%~dp0ComicPad.exe"
  exit /b
)
if exist "%~dp0python\pythonw.exe" (
  start "" "%~dp0python\pythonw.exe" "%~dp0server.py"
  exit /b
)
where pythonw >nul 2>nul
if %errorlevel%==0 (
  start "" pythonw "%~dp0server.py"
) else (
  start "ComicPad 서버" /min python "%~dp0server.py"
)
