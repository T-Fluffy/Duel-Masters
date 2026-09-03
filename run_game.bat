@echo off
REM Launch Duel Masters as a STANDALONE OS window (not the editor's embedded game view).
REM The embedded view used by the editor's Game panel cannot be resized, so the
REM Fullscreen/Windowed toggle only works when the game runs as a real window like this.
setlocal
cd /d "%~dp0"
set "GODOT=D:\Godot projects\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
if not exist "%GODOT%" (
    echo Godot 4.7.2 mono not found at:
    echo   %GODOT%
    echo Edit this .bat and set GODOT to your Godot_v4.7.2-stable_mono_win64.exe path.
    pause
    exit /b 1
)
"%GODOT%" --path "%~dp0" %*
