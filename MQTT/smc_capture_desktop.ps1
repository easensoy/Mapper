# ============================================================
# SMC rig - live MQTT capture to the Desktop, millisecond-stamped,
# BOTH txt and jsonl at once. Runs the logger on the broker machine
# via localhost (127.0.0.1) - the broker's mosquitto.conf listens on
# 0.0.0.0:1883 so loopback reaches it. (The PLCs still publish to the
# broker's LAN IP 192.168.1.50, set in the Mapper's MqttConn URL - only
# the remote PLCs need the LAN IP; this local subscriber does not.)
#
#   mosquitto_sub -F "%t|%p"   -> emits  topic|payload  per message
#   ForEach-Object             -> stamps each with a PowerShell-generated
#                                 millisecond ISO timestamp and writes one
#                                 line to the .txt AND one JSON object to
#                                 the .jsonl, both on the Desktop.
#
# Start this BEFORE cycling the actuators. Stop with Ctrl+C.
#
# NOTE on the jsonl: payload is written UNQUOTED ("payload":2), which is
# valid JSON only while every payload is numeric (the component state
# numbers are). If a component ever publishes a non-numeric payload, quote
# it -> "payload":"$($parts[1])".
# ============================================================

$desktop = [Environment]::GetFolderPath("Desktop")
$txt   = Join-Path $desktop "smc_mqtt_log_ms.txt"
$jsonl = Join-Path $desktop "smc_mqtt_log_ms.jsonl"

& "C:\Program Files\mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "smc/#" -q 1 -F "%t|%p" | ForEach-Object {
    $ts    = Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffzzz"
    $parts = $_ -split "\|", 2
    $line  = "$ts|$($parts[0])|$($parts[1])"
    $json  = "{`"t`":`"$ts`",`"topic`":`"$($parts[0])`",`"payload`":$($parts[1])}"
    $line | Tee-Object -FilePath $txt -Append
    $json | Out-File -FilePath $jsonl -Append -Encoding utf8
}
