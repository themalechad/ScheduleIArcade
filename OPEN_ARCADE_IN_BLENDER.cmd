@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "BLEND=%ROOT%BlenderEditable\ScheduleIArcade_Editable.blend"
set "SCRIPT=%ROOT%BlenderTools\prepare_arcade_blender.py"

set "BLENDER="
for /f "delims=" %%I in ('where blender.exe 2^>nul') do if not defined BLENDER set "BLENDER=%%I"
if not defined BLENDER (
  for /f "delims=" %%I in ('powershell -NoProfile -Command "$p=Get-ChildItem -Path ''$env:ProgramFiles\Blender Foundation'' -Filter blender.exe -Recurse -ErrorAction SilentlyContinue ^| Sort-Object FullName -Descending ^| Select-Object -First 1 -ExpandProperty FullName; if($p){$p}"') do set "BLENDER=%%I"
)
if not defined BLENDER (
  echo Blender.exe was not found automatically.
  echo Open Blender manually and run BlenderTools\prepare_arcade_blender.py.
  pause
  exit /b 1
)

echo Blender: %BLENDER%
if exist "%BLEND%" (
  echo Opening existing editable arcade scene...
  start "" "%BLENDER%" "%BLEND%"
) else (
  echo Creating editable arcade scene from arcade.fbx...
  start "" "%BLENDER%" --python "%SCRIPT%" -- "%ROOT%"
)
endlocal
