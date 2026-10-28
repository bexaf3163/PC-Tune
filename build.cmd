@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4.8 compiler not found at %CSC%
    exit /b 1
)
"%CSC%" /nologo /target:winexe /out:PC-Tune.exe /optimize+ ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll /r:System.Management.dll ^
    /r:System.ServiceProcess.dll ^
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll *.cs
if errorlevel 1 exit /b %errorlevel%
echo [OK] Built portable executable: %~dp0PC-Tune.exe
