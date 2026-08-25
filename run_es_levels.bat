@echo off
REM Portable launcher: runs from whatever folder this .bat lives in, and finds
REM Python on PATH. No hard-coded username or Python location — works on any machine.
cd /d "%~dp0"
set PYTHONIOENCODING=utf-8

REM Use "python" if it's on PATH; fall back to the py launcher (ships with python.org installs).
where python >nul 2>&1
if %errorlevel%==0 (
    set "PY=python"
) else (
    set "PY=py"
)

"%PY%" es_full_levels.py > es_last_run.log 2>&1
"%PY%" compute_es_stats.py >> es_last_run.log 2>&1
