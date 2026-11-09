@echo off
rem Gera o instalador sem publicar na rede rede (suspensa ate liberacao). Saida em docs\instalador\ultimo-build.log
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" -SkipNetwork > "%~dp0..\docs\instalador\ultimo-build.log" 2>&1
