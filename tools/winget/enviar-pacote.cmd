@echo off
rem Primeiro envio do pacote WinStatusBar.WinStatusBar ao winget. Registro em publicar-winget.log, nesta pasta.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0enviar-pacote.ps1"
