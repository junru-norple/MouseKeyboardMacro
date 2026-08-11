@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "APP=%~dp0Program\App\Launcher\MacroLauncher.exe"
set "ROOT=%~dp0"
if exist "%APP%" goto launch
set "ROOT="
for /f "tokens=1,2,*" %%A in ('%SystemRoot%\System32\reg.exe query "HKCU\Software\MouseKeyboardMacro" /v InstallRoot 2^>nul') do if /I "%%A"=="InstallRoot" set "ROOT=%%C"
if not defined ROOT goto missing
set "APP=%ROOT%\Program\App\Launcher\MacroLauncher.exe"
if not exist "%APP%" goto missing
:launch
"%APP%" --tool player --mode elevated --project-root "%ROOT%"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" exit /b 0
echo Macro launcher failed with code %RC%.
echo See Program\State\Logs\launcher.log under the registered install root.
pause
exit /b %RC%
:missing
echo Macro tool install root was not found.
pause
exit /b 2
