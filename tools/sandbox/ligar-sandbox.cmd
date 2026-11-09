@echo off
rem Liga o Windows Sandbox (pede administrador). Resultado em ligar-sandbox.log, nesta pasta.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ligar-sandbox.ps1"
