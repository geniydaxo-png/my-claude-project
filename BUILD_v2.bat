@echo off
chcp 65001 >nul 2>&1
cd /d "%~dp0"
title Delphi MT60 Core Protocol v2 Compiler
echo ============================================================
echo   DELPHI MT60 CORE PROTOCOL v2 (XAVFSIZ) BUILDER
echo ============================================================
echo.

set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [XATO] .NET Kompilyatori topilmadi!
    pause
    exit /b 1
)

echo Kompilyatsiya: Delphi_MT60_Core_Protocol_v2.cs ...
"%CSC%" /nologo /platform:x86 /out:"Delphi_MT60_Core_Protocol_v2.exe" "Delphi_MT60_Core_Protocol_v2.cs"
if errorlevel 1 (
    echo [XATO] Kompilyatsiyada xatolik!
    pause
    exit /b 1
)

echo.
echo [TAYYOR] Delphi_MT60_Core_Protocol_v2.exe yig'ildi!
echo Eslatma: mt60_ram_bootloader.bin shu papkada bo'lsin.
echo.
pause
exit /b 0
