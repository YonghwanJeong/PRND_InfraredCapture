@echo off
setlocal

rem ============================================
rem CleanRelease.bat - Release 배포 정리
rem 위치: Root
rem 정리 대상: Root\Build\Release
rem ============================================

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "RELEASE_DIR=%ROOT%\Build\Release"

if not exist "%RELEASE_DIR%" (
  echo [ERROR] Not found: "%RELEASE_DIR%"
  exit /b 1
)

echo [Mode1] Cleaning non-runtime files in: "%RELEASE_DIR%"

rem 삭제할 확장자 목록
set EXT_LIST=pdb xml mdb ipdb iobj map chm log tmp cache tlog

for %%E in (%EXT_LIST%) do (
  echo   - Deleting *.%%E
  attrib -R -H -S /S /D "%RELEASE_DIR%\*.%%E" 2>nul
  del /F /S /Q "%RELEASE_DIR%\*.%%E" 2>nul
)

rem *.insp는 절대 삭제 금지

echo [Done] Release cleanup complete.
endlocal
pause
