@echo off
rem One-time setup of the Python environment the voice tools need (faster-whisper, PyAV, numpy, Pillow).
cd /d "%~dp0"
if not exist .venv python -m venv .venv
.venv\Scripts\python -m pip install -q --upgrade pip
.venv\Scripts\python -m pip install -q faster-whisper pillow numpy scipy
.venv\Scripts\python -c "import faster_whisper, av, numpy, PIL; print('venv ready')"
