@echo off
rem Gera o instalador, entrega em Instalador (este PC) e publica no GitHub (codigo + Release). Saida em docs\instalador\ultimo-build.log
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" > "%~dp0..\docs\instalador\ultimo-build.log" 2>&1
