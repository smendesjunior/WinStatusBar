@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0account-action.ps1" -Provider gemini -Action manage
