@echo off
setlocal
cd /d "%~dp0.."
if not exist ".venv-backend\Scripts\python.exe" (
  echo Create .venv-backend and install backend/requirements.txt first. See backend/README.md.
  pause
  exit /b 1
)
".venv-backend\Scripts\python.exe" -m uvicorn backend.main:app --host 127.0.0.1 --port 8767 --workers 1 --no-access-log
pause
