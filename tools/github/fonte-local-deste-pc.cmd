@echo off
rem Este PC se atualiza pela pasta Instalador (Mode Local). Resultado em fonte-local-deste-pc.log, nesta pasta.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fonte-local-deste-pc.ps1"
