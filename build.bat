@echo off
chcp 65001 >nul
rem ============================================================
rem  EyeCare 编译脚本：只用 Windows 自带的 .NET Framework 4.x 编译器
rem  产物：EyeCare.exe（与 config.ini 放同一目录）
rem ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"
set CSC=
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not defined CSC (
  echo [X] 未找到系统自带的 C# 编译器，请确认已安装 .NET Framework 4.8（Win10/11 默认自带）。
  exit /b 1
)
echo [*] 使用编译器: !CSC!
if exist EyeCare.exe del /q EyeCare.exe
!CSC! /nologo /target:winexe /optimize+ /platform:anycpu /out:EyeCare.exe /win32icon:app.ico /win32manifest:app.manifest /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll EyeCare.cs
if errorlevel 1 (
  echo [X] 编译失败，见上方错误行号。
  exit /b 1
)
echo [OK] 已生成 EyeCare.exe
echo [*] 运行计时状态机自检...
EyeCare.exe --selftest
if errorlevel 1 (
  echo [X] 自检未通过，详情见 selftest-report.txt
  exit /b 1
)
echo [OK] 自检全部通过。
echo.
echo 下一步：双击 EyeCare.exe 启动，然后在托盘图标右键勾选「开机自动启动」。
echo 想改提醒间隔：编辑 config.ini（保存即生效），或托盘右键「设置…」。
endlocal
