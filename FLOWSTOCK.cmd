@echo off
chcp 65001 >nul
setlocal

set "ROOT=D:\FlowStock"

set "SLN_REL=apps\windows\FlowStock.sln"
set "SERVER_PROJ_REL=apps\windows\FlowStock.Server\FlowStock.Server.csproj"
set "APP_PROJ_REL=apps\windows\FlowStock.App\FlowStock.App.csproj"
set "TSD_SIM_REL=tools\flowstock-scan-simulator\tsd-scanner-sim.ps1"

set "SLN_FULL=%ROOT%\%SLN_REL%"
set "SERVER_PROJ_FULL=%ROOT%\%SERVER_PROJ_REL%"
set "APP_PROJ_FULL=%ROOT%\%APP_PROJ_REL%"
set "TSD_SIM_FULL=%ROOT%\%TSD_SIM_REL%"

set "BASE_URL=https://127.0.0.1:7154"

cd /d "%ROOT%" || (
echo Не удалось перейти в %ROOT%
pause
exit /b 1
)

if /i "%~1"=="both" goto run_both
if /i "%~1"=="all" goto run_all
if /i "%~1"=="server" goto run_server
if /i "%~1"=="app" goto run_app
if /i "%~1"=="tsd" goto run_tsd_sim
if /i "%~1"=="sim" goto run_tsd_sim
if /i "%~1"=="build" goto rebuild_and_run_both
if /i "%~1"=="buildall" goto rebuild_and_run_all
if /i "%~1"=="check" (
set "NO_PAUSE=1"
goto check_paths
)
if /i "%~1"=="test" (
set "NO_PAUSE=1"
goto check_paths
)

echo.
echo ==============================
echo FlowStock launcher
echo ==============================
echo.
echo 1. Запуск FlowStock Server + App
echo 2. Запуск FlowStock Server
echo 3. Запуск FlowStock App
echo 4. Пересборка FlowStock.sln + запуск Server + App
echo 5. Запуск TSD scanner simulator
echo 6. Запуск FlowStock Server + App + TSD scanner simulator
echo 7. Пересборка FlowStock.sln + запуск Server + App + TSD scanner simulator
echo 8. Проверка путей и PowerShell 7
echo.
set /p choice="Выбери пункт: "

if "%choice%"=="1" goto run_both
if "%choice%"=="2" goto run_server
if "%choice%"=="3" goto run_app
if "%choice%"=="4" goto rebuild_and_run_both
if "%choice%"=="5" goto run_tsd_sim
if "%choice%"=="6" goto run_all
if "%choice%"=="7" goto rebuild_and_run_all
if "%choice%"=="8" goto check_paths

echo.
echo Неверный выбор.
pause
exit /b 1

:rebuild_and_run_both
echo.
echo Пересобираю FlowStock solution...
dotnet build "%SLN_FULL%"

if errorlevel 1 (
echo.
echo ОШИБКА: сборка завершилась с ошибкой. Server/App не запускаю.
pause
exit /b 1
)

echo.
echo Сборка успешно завершена.
goto run_both

:rebuild_and_run_all
echo.
echo Пересобираю FlowStock solution...
dotnet build "%SLN_FULL%"

if errorlevel 1 (
echo.
echo ОШИБКА: сборка завершилась с ошибкой. Server/App/TSD simulator не запускаю.
pause
exit /b 1
)

echo.
echo Сборка успешно завершена.
goto run_all

:run_both
call :start_server
call :wait_server
call :start_app

echo.
echo Готово.
exit /b 0

:run_all
call :start_server
call :wait_server
call :start_app
call :start_tsd_sim
if errorlevel 1 exit /b 1

echo.
echo Готово.
exit /b 0

:run_server
call :start_server
exit /b 0

:run_app
call :start_app
exit /b 0

:run_tsd_sim
call :start_tsd_sim
exit /b %errorlevel%

:start_server
echo.
echo Запускаю FlowStock Server...
start "FlowStock Server" powershell -NoLogo -NoExit -ExecutionPolicy Bypass -Command "cd '%ROOT%'; dotnet run --project '%SERVER_PROJ_FULL%'"
exit /b 0

:wait_server
echo.
echo Жду готовности сервера %BASE_URL% ...
for /L %%i in (1,1,30) do (
curl.exe -kfsS "%BASE_URL%/api/version" >nul 2>nul
if not errorlevel 1 goto server_ready
timeout /t 1 /nobreak >nul
)

echo.
echo Сервер не ответил за 30 секунд. Продолжаю запуск.
exit /b 0

:server_ready
echo Сервер готов.
exit /b 0

:start_app
echo.
echo Запускаю FlowStock App...
start "FlowStock App" powershell -NoLogo -NoExit -ExecutionPolicy Bypass -Command "cd '%ROOT%'; dotnet run --project '%APP_PROJ_FULL%'"
exit /b 0

:start_tsd_sim
echo.
echo Запускаю TSD scanner simulator...

if not exist "%TSD_SIM_FULL%" (
echo ОШИБКА: не найден файл симулятора:
echo %TSD_SIM_FULL%
pause
exit /b 1
)

call :resolve_pwsh
if errorlevel 1 (
echo.
echo ОШИБКА: PowerShell 7 / pwsh.exe не найден.
echo Для корректного русского текста нужен PowerShell 7.
echo.
echo Можно запустить вручную после установки PowerShell 7:
echo pwsh.exe -NoLogo -NoExit -ExecutionPolicy Bypass -File "%TSD_SIM_FULL%"
echo.
pause
exit /b 1
)

start "TSD Scanner Simulator" "%PWSH_EXE%" -NoLogo -NoExit -ExecutionPolicy Bypass -File "%TSD_SIM_FULL%"
exit /b 0

:resolve_pwsh
set "PWSH_EXE="

for /f "delims=" %%P in ('where pwsh.exe 2^>nul') do (
if not defined PWSH_EXE set "PWSH_EXE=%%P"
)

if not defined PWSH_EXE (
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" (
set "PWSH_EXE=%ProgramFiles%\PowerShell\7\pwsh.exe"
)
)

if not defined PWSH_EXE (
if exist "%LOCALAPPDATA%\Microsoft\powershell\7\pwsh.exe" (
set "PWSH_EXE=%LOCALAPPDATA%\Microsoft\powershell\7\pwsh.exe"
)
)

if defined PWSH_EXE (
exit /b 0
)

exit /b 1

:check_paths
echo.
echo ==============================
echo FlowStock launcher check
echo ==============================
echo.

echo ROOT:
echo %ROOT%
echo.

echo SLN_FULL:
echo %SLN_FULL%
echo.

echo SERVER_PROJ_FULL:
echo %SERVER_PROJ_FULL%
echo.

echo APP_PROJ_FULL:
echo %APP_PROJ_FULL%
echo.

echo TSD_SIM_FULL:
echo %TSD_SIM_FULL%
echo.

if exist "%ROOT%" (
echo OK: ROOT найден
) else (
echo ERROR: ROOT не найден
)

if exist "%SLN_FULL%" (
echo OK: solution найден
) else (
echo ERROR: solution не найден: %SLN_FULL%
)

if exist "%SERVER_PROJ_FULL%" (
echo OK: server project найден
) else (
echo ERROR: server project не найден: %SERVER_PROJ_FULL%
)

if exist "%APP_PROJ_FULL%" (
echo OK: app project найден
) else (
echo ERROR: app project не найден: %APP_PROJ_FULL%
)

if exist "%TSD_SIM_FULL%" (
echo OK: TSD simulator найден
) else (
echo ERROR: TSD simulator не найден: %TSD_SIM_FULL%
)

call :resolve_pwsh
if errorlevel 1 (
echo ERROR: pwsh.exe / PowerShell 7 не найден
) else (
echo OK: pwsh.exe найден: %PWSH_EXE%
)

echo.
echo Проверка завершена.
if not defined NO_PAUSE pause
exit /b 0
