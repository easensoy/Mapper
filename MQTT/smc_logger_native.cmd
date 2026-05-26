@echo off
REM ============================================================
REM SMC rig - paho-mqtt direct logger (no pipe buffering)
REM ------------------------------------------------------------
REM Replacement for smc_logger_desktop.cmd. The old .cmd piped
REM mosquitto_sub.exe into python dual_logger.py; mosquitto_sub
REM block-buffers its stdout when piped (~4-8 KB), so small state
REM events sit in its buffer for hours instead of reaching the
REM disk. The broker delivers them on time - they just never make
REM it through the pipe.
REM
REM This script runs paho-mqtt directly inside Python: every
REM on_message callback writes both Desktop files inline, no
REM external pipe in the way.
REM
REM Same end-to-end no-loss guarantees as the .cmd:
REM   client_id=smc_logger_desktop_py, clean_session=false, qos=1
REM   -> the broker queues for this exact client while it is
REM      offline and redelivers on restart.
REM
REM Each event lands in:
REM   %USERPROFILE%\Desktop\smc_log.jsonl   (raw NDJSON)
REM   %USERPROFILE%\Desktop\smc_log.txt     (human-readable)
REM
REM Ctrl-C stops cleanly; a session-end banner with the event
REM count is written to both files.
REM
REM First-time setup (once per machine):
REM   pip install paho-mqtt
REM ============================================================

python "%~dp0smc_logger_native.py"
