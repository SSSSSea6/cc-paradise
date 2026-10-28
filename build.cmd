@echo off
rem Builds ClaudeSwitcher.exe next to this file with the compiler that ships with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /win32icon:icons\switcher.ico /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /r:Microsoft.CSharp.dll /out:ClaudeSwitcher.exe ClaudeSwitcher.cs
