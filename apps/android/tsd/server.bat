@echo off
chcp 65001 >nul
title LightWMS TSD HTTP Server

set "ROOT=D:\LightWMS-local\apps\android\tsd"
set "PY=C:\Users\ЧестныйЗнак\AppData\Local\Programs\Python\Python313\python.exe"

cd /d "%ROOT%"

echo Starting LightWMS server with detailed logging...
echo Directory: "%CD%"
echo.

echo IPv4 addresses on this PC:
ipconfig | findstr /R /C:"IPv4-адрес" /C:"IPv4 Address"
echo.

echo TIP: Use the LAN IP (usually 10.x / 192.168.x) from the list above:
echo      http://^<LAN_IP^>:8080/
echo.

"%PY%" server.py

echo.
echo Server stopped.
pause
