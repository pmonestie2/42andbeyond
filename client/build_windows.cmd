@echo off
setlocal
where py >nul 2>nul
if %errorlevel%==0 (
    py -3 "%~dp0Tools\build_windows.py" %*
) else (
    python "%~dp0Tools\build_windows.py" %*
)
exit /b %errorlevel%
