@echo off
rem Envia ao GitHub mudancas so de texto e imagens (README, changelogs, capturas), sem versao nova. Registro em enviar-textos.log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0enviar-textos.ps1"
