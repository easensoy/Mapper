# SMC rig — MQTT capture commands (mosquitto)

Capture every component's state/status off the broker and save it as **JSONL** (one
JSON object per line, machine-readable) or **TXT** (human-readable). The Mapper makes
each actuator/sensor CAT publish `smc/<component>/state` through the BX1 `MQTT_CONNECTION`
(broker `192.168.1.50:1883`, topic root `smc`, QoS 1), so a single `smc/#` wildcard
subscription captures the whole rig.

> Paths assume mosquitto is installed at `C:\Program Files\Mosquitto\` and this repo is at
> `C:\VueOneMapper`.
>
> **`-h` host:** the **PLCs** publish to the broker's LAN IP `192.168.1.50` (set in the
> Mapper's `MqttConn` URL — they're remote and can't reach loopback). A **logger running on
> the broker machine** can use `127.0.0.1` (localhost) instead, because `mosquitto.conf`
> listens on `0.0.0.0:1883` (all interfaces, loopback included). Both reach the same broker;
> keep the listener on `0.0.0.0` so the PLCs aren't locked out. The commands below use
> `127.0.0.1`; swap for `192.168.1.50` if you log from another machine on the LAN.
>
> For a millisecond-stamped logger that writes BOTH `.txt` and `.jsonl` to your Desktop in
> one go, run **`smc_capture_desktop.ps1`** (PowerShell) — see §3b.

---

## 1. Start the broker

```bat
"C:\Program Files\Mosquitto\mosquitto.exe" -v -c C:\VueOneMapper\MQTT\mosquitto.conf
```

`-v` prints every connect / subscribe / publish so you can watch the PLCs connect live.
The config listens on `0.0.0.0:1883` (so the PLCs can reach it), allows anonymous (TEST
only), and persists QoS-1 backlog under `MQTT\data\`.

---

## 2. Capture ALL component states → JSONL  (durable, lossless)

```bat
"C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -p 1883 -t "smc/#" -q 1 -i smc_logger -c -F "%J" >> "C:\VueOneMapper\MQTT\smc_log.jsonl"
```

(This is exactly what `smc_logger.cmd` runs — in a `.cmd` the `%J` must be written `%%J`.)

- `-t "smc/#"` — every SMC component topic (wildcard).
- `-q 1` — subscribe at QoS 1 (at-least-once).
- `-i smc_logger -c` — FIXED client id + persistent session: the broker QUEUES messages
  for this exact logger while it is offline and REDELIVERS them on restart (lossless across
  the logger's own downtime). Combined with QoS 1 + the PLC's `MQTT_CONNECTION` queue depth,
  this is end-to-end no-loss: PLC → broker → file.
- `-F "%J"` — emit the whole message as one JSON object per line.

Each line:

```json
{"tst":"2026-06-16T14:08:22.531000+0100","topic":"smc/feeder/state","qos":1,"retain":false,"payloadlen":1,"payload":"2"}
```

Start it **before** cycling the actuators. Stop with `Ctrl+C`.

---

## 3. Capture ALL component states → TXT  (human-readable)

```bat
"C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -p 1883 -t "smc/#" -q 1 -F "%I  %t  %p" >> "C:\VueOneMapper\MQTT\smc_log.txt"
```

(Ready-to-run as `smc_capture_txt.cmd`.) `%I` = ISO-8601 timestamp, `%t` = topic, `%p` =
payload. Each line:

```
2026-06-16T14:08:22+0100  smc/feeder/state  2
```

---

## 3b. Capture to the Desktop, MILLISECOND-stamped, TXT **and** JSONL at once (PowerShell)

```powershell
powershell -ExecutionPolicy Bypass -File C:\VueOneMapper\MQTT\smc_capture_desktop.ps1
```

Runs `mosquitto_sub -F "%t|%p"` and, per message, stamps a PowerShell millisecond ISO
timestamp and writes BOTH files on your Desktop in one pass:

- `smc_mqtt_log_ms.txt`   → `2026-06-16T14:08:22.531+01:00|smc/feeder/state|2`
- `smc_mqtt_log_ms.jsonl` → `{"t":"2026-06-16T14:08:22.531+01:00","topic":"smc/feeder/state","payload":2}`

The millisecond stamp is generated locally (finer than mosquitto's `%I`), and the payload is
written unquoted — valid JSON while payloads are numeric (the state numbers are). Connects via
`127.0.0.1` (the logger runs on the broker machine).

---

## 4. Live watch (no file, just the console)

```bat
"C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "smc/#" -v
```

`-v` prints `topic payload`. Good for confirming a component is publishing at all.

---

## 5. One component only

```bat
"C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "smc/bearing_pnp/state" -v
```

Swap the topic for any component, e.g. `smc/clamp/state`, `smc/coverpnp_vr/state`,
`smc/shaft_hr/state`, `smc/checker/state`.

---

## 6. Tail / inspect the JSONL while it grows (PowerShell)

```powershell
Get-Content C:\VueOneMapper\MQTT\smc_log.jsonl -Wait -Tail 20
```

Pretty-print + filter one component with `jq` (if installed):

```bash
tail -f C:/VueOneMapper/MQTT/smc_log.jsonl | jq 'select(.topic=="smc/bearing_pnp/state") | {tst, payload}'
```

---

## Topics by component (`smc/<component>/state`)

The component name is the lowercased CAT instance name. Typical rig set:

- **M262 (Feed):** `feeder`, `checker`, `transfer`, `ejector`, `part_at_assembly`, `part_in_hopper`
- **M580 (Assembly/Disassembly):** `bearing_pnp`, `bearing_gripper`, `shaft_hr`, `shaft_vr`, `shaft_gripper`, `clamp`, `bearing_sensor`
- **BX1 (Covers):** `coverpnp_hr`, `coverpnp_vr`, `coverpnp_gripper`, `top_cover_senosr`

> On the **rig**, MQTT runs on BX1 only (M262/M580 firmware is RC50-gated). Which topics
> actually appear depends on the current MQTT wiring; `smc/#` captures whatever publishes,
> so the capture command never needs changing when components are added or moved.
