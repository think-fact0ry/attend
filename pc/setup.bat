@echo off
rem Korean messages live in install.ps1 (batch is read in the system codepage; keep this file ASCII).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
