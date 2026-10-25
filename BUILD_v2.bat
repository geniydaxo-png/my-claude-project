@echo off
chcp 65001 >nul 2>&1
cd /d "%~dp0"
title JAM DELPHI MT60 MASTER v2 - Builder
echo ============================================================
echo   JAM DELPHI MT60 MASTER v2 (GUI) BUILDER
echo ============================================================
echo.

set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [XATO] .NET Framework 4.0 kompilyatori topilmadi!
    pause
    exit /b 1
)

echo Kompilyatsiya qilinmoqda (GUI + protokol)...
"%CSC%" /nologo /target:winexe /platform:x86 /out:"MT60_Master_v2.exe" ^
 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
 "Delphi_MT60_Core_Protocol_v2.cs" "MT60_Master_GUI.cs"

if errorlevel 1 (
    echo.
    echo [XATO] Kompilyatsiyada xatolik!
    pause
    exit /b 1
)

echo.
echo [TAYYOR] MT60_Master_v2.exe yig'ildi!
echo Eslatma: mt60_ram_bootloader.bin shu papkada bo'lsin.
echo.
pause
exit /b 0
