@echo off
setlocal
pushd "%~dp0"

echo ============================================================
echo LYFZ NetDiag Electron - Windows release build
echo ============================================================
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-electron-release.ps1" %*
set "LYFZ_BUILD_EXIT=%ERRORLEVEL%"

if not "%LYFZ_BUILD_EXIT%"=="0" (
  echo.
  echo Build failed. See the error message above.
) else (
  echo.
  echo Build completed successfully.
)

popd
pause
exit /b %LYFZ_BUILD_EXIT%
