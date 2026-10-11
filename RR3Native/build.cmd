@echo off
rem Build Road Rash 3 native: recompile ROM -> C#, then compile with the .NET Framework C# compiler
setlocal
cd /d "%~dp0"
python tools\build68k.py || exit /b 1
if exist tools\buildz80.py python tools\buildz80.py || exit /b 1
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist bin mkdir bin
"%CSC%" /nologo /optimize+ /nowarn:162,219,168,414,169 /platform:x64 /target:winexe /out:bin\RR3.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll runtime\*.cs || exit /b 1
"%CSC%" /nologo /optimize+ /nowarn:162,219,168,414,169 /platform:x64 /target:exe /out:bin\RR3_console.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll runtime\*.cs || exit /b 1
if not exist bin\rr3.bin copy /y rr3.bin bin\rr3.bin >nul
echo Build OK: bin\RR3.exe
