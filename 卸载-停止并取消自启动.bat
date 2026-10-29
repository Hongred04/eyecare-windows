@echo off
chcp 65001 >nul
rem 退出程序 + 取消开机自启动
cd /d "%~dp0"
if exist EyeCare.exe EyeCare.exe --stop
timeout /t 2 /nobreak >nul
taskkill /f /im EyeCare.exe >nul 2>&1
if exist EyeCare.exe EyeCare.exe --disable-autostart
if exist EyeCare.exe EyeCare.exe --autostart-status
echo [OK] 已退出并取消开机自启动。程序文件与 log 记录仍保留，需要时可整个目录删除。
pause
