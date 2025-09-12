@echo off
setlocal

rem ============================================
rem CleanBuildAll.bat - Debug/Release 전체 정리
rem 위치: Root
rem 정리 대상: Root\Build\Debug, Root\Build\Release
rem   *.insp 제외 모든 파일 삭제
rem ============================================

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "DEBUG_DIR=%ROOT%\Build\Debug"
set "RELEASE_DIR=%ROOT%\Build\Release"

echo [Mode2] Deleting everything except *.insp in:
echo   - "%DEBUG_DIR%"
echo   - "%RELEASE_DIR%"

call :DeleteAllFilesExceptINSP "%DEBUG_DIR%"
call :DeleteAllFilesExceptINSP "%RELEASE_DIR%"

echo [Done] Bulk clean complete.
endlocal
pause
exit /b 0


:DeleteAllFilesExceptINSP
set "TARGET=%~1"
if "%TARGET%"=="" goto :eof
if not exist "%TARGET%" (
  echo   [Skip] Not found: "%TARGET%"
  goto :eof
)

echo   [Target] "%TARGET%"

rem 모든 파일 순회하며 확장자가 .insp가 아니면 삭제
for /r "%TARGET%" %%F in (*.*) do (
  if /I not "%%~xF"==".insp" (
    attrib -R -H -S "%%F" 2>nul
    del /F /Q "%%F" 2>nul
  )
)

rem 비어있는 폴더 제거
for /f "delims=" %%D in ('dir /ad /b /s "%TARGET%" ^| sort /R') do (
  rd "%%D" 2>nul
)

goto :eof
