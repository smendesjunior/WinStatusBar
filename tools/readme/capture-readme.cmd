@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0capture-readme.ps1" %*
