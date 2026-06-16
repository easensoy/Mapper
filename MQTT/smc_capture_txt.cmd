@echo off
REM ============================================================
REM SMC rig - human-readable TXT capture of every component state
REM ------------------------------------------------------------
REM Subscribes to every SMC state topic and appends one timestamped
REM line per message to smc_log.txt. Sister of smc_logger.cmd (which
REM writes JSONL); use whichever format you want. See capture_commands.md.
REM
REM   -h 192.168.1.50   broker host (change to your broker IP)
REM   -p 1883           broker port
REM   -t "smc/#"        every SMC component topic (wildcard)
REM   -q 1              subscribe at QoS 1 (at-least-once)
REM   -F "%%I  %%t  %%p"  one line: ISO-timestamp  topic  payload
REM                     (in a .cmd, %% escapes to a literal % for
REM                     mosquitto's format specifier).
REM
REM Each line looks like:
REM   2026-06-16T14:08:22+0100  smc/feeder/state  2
REM
REM Start this BEFORE cycling the actuators. Stop with Ctrl+C.
REM ============================================================
"C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 192.168.1.50 -p 1883 -t "smc/#" -q 1 -F "%%I  %%t  %%p" >> "C:\VueOneMapper\MQTT\smc_log.txt"
