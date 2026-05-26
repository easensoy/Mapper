@echo off
REM ============================================================
REM SMC rig - durable dual logger -> Desktop (JSON + TXT)
REM ------------------------------------------------------------
REM Subscribes once to smc/# at QoS1 and tees every event into
REM two files on the operator's Desktop:
REM
REM   %USERPROFILE%\Desktop\smc_log.jsonl   (raw NDJSON)
REM   %USERPROFILE%\Desktop\smc_log.txt     (human-readable)
REM
REM Both are append-mode so consecutive sessions accumulate. A
REM session-start banner is written at the head of each file so
REM individual rig runs are easy to find in the accumulated log.
REM Delete or rename the files between runs if you want a clean
REM session per test.
REM
REM Durability flags (identical to smc_logger.cmd):
REM   -i smc_logger_desktop  FIXED client id so the broker QUEUES
REM                          messages for THIS subscriber while it
REM                          is offline and REDELIVERS on restart.
REM   -c                     ... + disable clean session
REM   -q 1                   subscribe at QoS1 (at-least-once)
REM   -F "%%J"               emit each message as one JSON object
REM                          (in a .cmd, %% escapes to a literal %)
REM
REM Combined with QoS1 + QueueDepth=100 on the PLC's
REM MQTT_CONNECTION and persistence=true on the broker, this is
REM end-to-end lossless: PLC -> broker -> Desktop files, with a
REM buffer at every hop.
REM
REM HOW TO USE
REM   1. Start the broker (see RUNBOOK.txt, STEP 1).
REM   2. Start this logger BEFORE clicking Test Runtime in Mapper.
REM   3. Click Test Runtime; cycle Feeder/Checker/Transfer on the rig.
REM   4. Ctrl-C to stop. Both Desktop files are ready to analyse.
REM ============================================================

"C:\Program Files\Mosquitto\mosquitto_sub.exe" ^
    -h 192.168.1.50 -p 1883 ^
    -t "smc/#" ^
    -q 1 ^
    -i smc_logger_desktop -c ^
    -F "%%J" ^
  | python "%~dp0dual_logger.py"
