@echo off
chcp 65001 >nul
rem 编译 + 开机自启动，一步装好
cd /d "%~dp0"
call build.bat
if errorlevel 1 exit /b 1
if not exist EyeCare.exe (
  echo [X] 找不到 EyeCare.exe
  exit /b 1
)
EyeCare.exe --enable-autostart
EyeCare.exe --autostart-status
start "" EyeCare.exe --min
echo [OK] 已启动，并已设置为开机自启动。
pause
