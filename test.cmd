@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist tests\out mkdir tests\out
"%CSC%" /nologo /target:exe /out:tests\out\PC-Tune.Tests.exe /main:PCTune.RlTests ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll ^
    /r:System.ServiceProcess.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll *.cs tests\*.cs
if errorlevel 1 exit /b %errorlevel%
tests\out\PC-Tune.Tests.exe tests\fixtures\117hd_TESTHASH.jar
