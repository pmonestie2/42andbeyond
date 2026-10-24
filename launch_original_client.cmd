@echo off
pushd "%~dp0"
"C:\PYTHON\python.exe" -m tools.windows.start_proxy %*
set "launch_exit=%errorlevel%"
popd
exit /b %launch_exit%
