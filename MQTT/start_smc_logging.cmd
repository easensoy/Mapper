@echo off
REM One-command launcher: starts the MQTT broker in its own window, then runs the
REM JSONL + SQLite logger (which subscribes to smc/# and writes to the desktop
REM Output folder). Double-click this file, or run it from a terminal.
REM If a broker is already running, the second one just fails to bind 1883 (harmless).
title SMC MQTT logging
echo [1/2] starting MQTT broker...
start "SMC MQTT Broker" "C:\Program Files\mosquitto\mosquitto.exe" -c "C:\VueOneMapper\MQTT\mosquitto.conf" -v
echo       waiting for broker to come up...
timeout /t 2 /nobreak >nul
echo [2/2] starting JSONL + SQLite logger  (Ctrl+C to stop)
python "C:\VueOneMapper\MQTT\mqtt_to_sqlite.py"
