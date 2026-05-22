@echo off
REM ============================================================
REM SMC rig - durable, lossless JSONL logger (no database)
REM ------------------------------------------------------------
REM Subscribes to every SMC state topic and appends one JSON object
REM per line (JSONL / NDJSON) to smc_log.jsonl. No DB required.
REM
REM   -h 192.168.1.50   broker host (change to your broker IP)
REM   -p 1883           broker port
REM   -t "smc/#"        every SMC topic (wildcard)
REM   -q 1              subscribe at QoS 1 (at-least-once)
REM   -i smc_logger     FIXED client id ...
REM   -c                ... + disable clean session => the broker
REM                     QUEUES messages for this exact logger while it
REM                     is offline and REDELIVERS them on restart.
REM                     This makes the logger itself lossless across
REM                     its own downtime.
REM   -F "%%J"          emit the whole message as one JSON object/line
REM                     (in a .cmd, %% escapes to a literal % for
REM                     mosquitto's format specifier).
REM
REM Each line looks like:
REM   {"tst":"...","topic":"smc/feeder/state","qos":1,"retain":false,"payload":"2"}
REM
REM Combined with QoS1 + QueueDepth on the PLC's MQTT_CONNECTION,
REM this gives end-to-end no-loss: PLC -> broker -> file, buffered
REM at each hop. Start this BEFORE cycling the actuators.
REM ============================================================
"C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 192.168.1.50 -p 1883 -t "smc/#" -q 1 -i smc_logger -c -F "%%J" >> "C:\VueOneMapper\MQTT\smc_log.jsonl"
