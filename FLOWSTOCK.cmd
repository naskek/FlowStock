@echo off
chcp 65001 >nul
setlocal EnableExtensions

rem ============================================================
rem FlowStock roots
rem ============================================================

set "ROOT_MAIN=D:\FlowStock"
set "ROOT_DEV=D:\FlowStock-dev"

set "SLN_REL=apps\windows\FlowStock.sln"
set "SERVER_PROJ_REL=apps\windows\FlowStock.Server\FlowStock.Server.csproj"
set "APP_PROJ_REL=apps\windows\FlowStock.App\FlowStock.App.csproj"
set "TSD_SIM_REL=tools\flowstock-scan-simulator\tsd-scanner-sim.ps1"
set "WPF_LAUNCHER_REL=tools\windows\start-flowstock-wpf.ps1"

set "BASE_URL=https://127.0.0.1:7154"

rem По умолчанию — основной checkout
call :use_main

rem ============================================================
rem Command line arguments
rem ============================================================

if /i "%~1"=="both" goto run_both
if /i "%~1"=="all" goto run_all
if /i "%~1"=="server" goto run_server
if /i "%~1"=="app" goto run_app
if /i "%~1"=="tsd" goto run_tsd_sim
if /i "%~1"=="sim" goto run_tsd_sim
if /i "%~1"=="build" goto rebuild_and_run_both
if /i "%~1"=="buildall" goto rebuild_and_run_all

rem DEV shortcuts
if /i "%~1"=="dev" (
    call :use_dev
    goto run_both
)

if /i "%~1"=="devserver" (
    call :use_dev
    goto run_server
)

if /i "%~1"=="devapp" (
    call :use_dev
    goto run_app
)

if /i "%~1"=="devall" (
    call :use_dev
    goto run_all
)

if /i "%~1"=="devbuild" (
    call :use_dev
    goto rebuild_and_run_both
)

if /i "%~1"=="devbuildall" (
    call :use_dev
    goto rebuild_and_run_all
)

if /i "%~1"=="check" (
    set "NO_PAUSE=1"
    goto check_paths
)

if /i "%~1"=="devcheck" (
    call :use_dev
    set "NO_PAUSE=1"
    goto check_paths
)

rem ============================================================
rem Interactive menu
rem ============================================================

:menu
echo.
echo ==========================================
echo FlowStock launcher
echo ==========================================
echo.
echo MAIN: %ROOT_MAIN%
echo DEV : %ROOT_DEV%
echo.
echo ---------- MAIN ----------
echo 1. MAIN: Server + WPF
echo 2. MAIN: Server
echo 3. MAIN: WPF
echo 4. MAIN: Build + Server + WPF
echo 5. MAIN: TSD scanner simulator
echo 6. MAIN: Server + WPF + TSD simulator
echo 7. MAIN: Build + Server + WPF + TSD simulator
echo.
echo ---------- DEV ----------
echo 8. DEV: Server + WPF
echo 9. DEV: Server
echo 10. DEV: WPF
echo 11. DEV: Build + Server + WPF
echo 12. DEV: Server + WPF + TSD simulator
echo 13. DEV: Build + Server + WPF + TSD simulator
echo.
echo ---------- SERVICE ----------
echo 14. Проверка MAIN путей и PowerShell 7
echo 15. Проверка DEV путей и PowerShell 7
echo.
set /p choice="Выбери пункт: "

if "%choice%"=="1" (
    call :use_main
    goto run_both
)

if "%choice%"=="2" (
    call :use_main
    goto run_server
)

if "%choice%"=="3" (
    call :use_main
    goto run_app
)

if "%choice%"=="4" (
    call :use_main
    goto rebuild_and_run_both
)

if "%choice%"=="5" (
    call :use_main
    goto run_tsd_sim
)

if "%choice%"=="6" (
    call :use_main
    goto run_all
)

if "%choice%"=="7" (
    call :use_main
    goto rebuild_and_run_all
)

if "%choice%"=="8" (
    call :use_dev
    goto run_both
)

if "%choice%"=="9" (
    call :use_dev
    goto run_server
)

if "%choice%"=="10" (
    call :use_dev
    goto run_app
)

if "%choice%"=="11" (
    call :use_dev
    goto rebuild_and_run_both
)

if "%choice%"=="12" (
    call :use_dev
    goto run_all
)

if "%choice%"=="13" (
    call :use_dev
    goto rebuild_and_run_all
)

if "%choice%"=="14" (
    call :use_main
    goto check_paths
)

if "%choice%"=="15" (
    call :use_dev
    goto check_paths
)

echo.
echo Неверный выбор.
pause
exit /b 1

rem ============================================================
rem Root selection
rem ============================================================

:use_main
set "ENV_NAME=MAIN"
set "ROOT=%ROOT_MAIN%"
call :resolve_paths
exit /b 0

:use_dev
set "ENV_NAME=DEV"
set "ROOT=%ROOT_DEV%"
call :resolve_paths
exit /b 0

:resolve_paths
set "SLN_FULL=%ROOT%\%SLN_REL%"
set "SERVER_PROJ_FULL=%ROOT%\%SERVER_PROJ_REL%"
set "APP_PROJ_FULL=%ROOT%\%APP_PROJ_REL%"
set "TSD_SIM_FULL=%ROOT%\%TSD_SIM_REL%"
set "WPF_LAUNCHER_FULL=%ROOT_MAIN%\%WPF_LAUNCHER_REL%"
exit /b 0

rem ============================================================
rem Build
rem ============================================================

:rebuild_and_run_both
call :ensure_root
if errorlevel 1 exit /b 1

echo.
echo [%ENV_NAME%] Пересобираю FlowStock solution...
dotnet build "%SLN_FULL%"

if errorlevel 1 (
    echo.
    echo ОШИБКА: сборка завершилась с ошибкой.
    echo Server/WPF не запускаю.
    pause
    exit /b 1
)

echo.
echo Сборка успешно завершена.
goto run_both

:rebuild_and_run_all
call :ensure_root
if errorlevel 1 exit /b 1

echo.
echo [%ENV_NAME%] Пересобираю FlowStock solution...
dotnet build "%SLN_FULL%"

if errorlevel 1 (
    echo.
    echo ОШИБКА: сборка завершилась с ошибкой.
    echo Server/WPF/TSD simulator не запускаю.
    pause
    exit /b 1
)

echo.
echo Сборка успешно завершена.
goto run_all

rem ============================================================
rem Run modes
rem ============================================================

:run_both
call :ensure_root
if errorlevel 1 exit /b 1

call :start_server
call :wait_server
call :start_app

echo.
echo [%ENV_NAME%] Готово.
exit /b 0

:run_all
call :ensure_root
if errorlevel 1 exit /b 1

call :start_server
call :wait_server
call :start_app
call :start_tsd_sim

if errorlevel 1 exit /b 1

echo.
echo [%ENV_NAME%] Готово.
exit /b 0

:run_server
call :ensure_root
if errorlevel 1 exit /b 1

call :start_server
exit /b 0

:run_app
call :ensure_root
if errorlevel 1 exit /b 1

call :start_app
exit /b 0

:run_tsd_sim
call :ensure_root
if errorlevel 1 exit /b 1

call :start_tsd_sim
exit /b %errorlevel%

rem ============================================================
rem Start processes
rem ============================================================

:start_server
echo.
echo [%ENV_NAME%] Запускаю FlowStock Server...
echo ROOT: %ROOT%

start "FlowStock Server [%ENV_NAME%]" powershell ^
    -NoLogo ^
    -NoExit ^
    -ExecutionPolicy Bypass ^
    -Command "Set-Location -LiteralPath '%ROOT%'; dotnet run --project '%SERVER_PROJ_FULL%'"

exit /b 0

:wait_server
echo.
echo [%ENV_NAME%] Жду готовности сервера %BASE_URL% ...

for /L %%i in (1,1,30) do (
    curl.exe -kfsS "%BASE_URL%/api/version" >nul 2>nul
    if not errorlevel 1 goto server_ready
    timeout /t 1 /nobreak >nul
)

echo.
echo Сервер не ответил за 30 секунд.
echo Продолжаю запуск WPF.
exit /b 0

:server_ready
echo Сервер готов.
exit /b 0

:start_app
echo.
echo [%ENV_NAME%] Запускаю FlowStock WPF...
echo ROOT: %ROOT%

if /i "%ENV_NAME%"=="MAIN" (
    start "FlowStock WPF [MAIN]" powershell ^
        -NoLogo ^
        -ExecutionPolicy Bypass ^
        -File "%WPF_LAUNCHER_FULL%"
    exit /b 0
)

start "FlowStock WPF [%ENV_NAME%]" powershell ^
    -NoLogo ^
    -NoExit ^
    -ExecutionPolicy Bypass ^
    -Command "Set-Location -LiteralPath '%ROOT%'; dotnet run --project '%APP_PROJ_FULL%'"

exit /b 0

:start_tsd_sim
echo.
echo [%ENV_NAME%] Запускаю TSD scanner simulator...

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
    echo Можно запустить вручную:
    echo pwsh.exe -NoLogo -NoExit -ExecutionPolicy Bypass -File "%TSD_SIM_FULL%"
    echo.
    pause
    exit /b 1
)

start "TSD Scanner Simulator [%ENV_NAME%]" "%PWSH_EXE%" ^
    -NoLogo ^
    -NoExit ^
    -ExecutionPolicy Bypass ^
    -File "%TSD_SIM_FULL%"

exit /b 0

rem ============================================================
rem Helpers
rem ============================================================

:ensure_root
cd /d "%ROOT%" 2>nul

if errorlevel 1 (
    echo.
    echo ОШИБКА: не удалось перейти в:
    echo %ROOT%
    pause
    exit /b 1
)

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
    if exist "%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe" (
        set "PWSH_EXE=%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe"
    )
)

if defined PWSH_EXE exit /b 0
exit /b 1

rem ============================================================
rem Diagnostics
rem ============================================================

:check_paths
echo.
echo ==========================================
echo FlowStock launcher check [%ENV_NAME%]
echo ==========================================
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
echo WPF_LAUNCHER_FULL:
echo %WPF_LAUNCHER_FULL%
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

if exist "%WPF_LAUNCHER_FULL%" (
    echo OK: WPF launcher найден
) else (
    echo ERROR: WPF launcher не найден: %WPF_LAUNCHER_FULL%
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
