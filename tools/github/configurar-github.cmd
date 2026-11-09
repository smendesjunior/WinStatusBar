@echo off
rem Prepara este PC para publicar no GitHub (GitHub CLI, login, git push, textos do repositorio).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0configurar-github.ps1"
