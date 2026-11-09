@echo off
rem Gera o instalador de teste para o Windows Sandbox (sem Instalador e sem GitHub). Saida em gerar-teste.log, nesta pasta.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\build-release.ps1" -SandboxTest > "%~dp0gerar-teste.log" 2>&1
