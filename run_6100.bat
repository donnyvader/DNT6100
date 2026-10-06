@echo off
setlocal
cd /d "%~dp0"
title DNT JIG DAILY INSPECTION - 6100
"%~dp0Dnt.JigDaily.exe"
if errorlevel 1 pause
