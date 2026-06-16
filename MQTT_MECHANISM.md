# MQTT mechanism — per-resource MQTT_CONNECTION + embedded CAT publish (read before touching MQTT)

> **🔒 HARD RULE — NO standalone bridge pairs. EVER.**
> There must be **ZERO** `MqttFmt_<comp>` / `MqttPub_<comp>` FBs in the generated
> syslay/sysres, and **NO** `MqttBridgeEmitter` / `EmitBridge` in the code. The
> standalone "bridge" (a formatter+publish pair per remote component on BX1) was
> built and deleted **many** times; the user has rejected it definitively. Do not
> recover it from git. The MQTT shape is: **one `MQTT_CONNECTION` per resource +
> the Formatter/Publish EMBEDDED inside each CAT**. Nothing else.

## The mechanism — what MUST be generated

1. **One `MQTT_CONNECTION` per resource** — `MqttConn` (BX1), `MqttConn_M262`
   (M262), `MqttConn_M580` (M580). Injected by
   `SystemLayoutInjector.InjectMqttConn`. Each gets a **UNIQUE `ConnectionID`
   AND `ClientIdentifier`** per resource (`SMC_BX1` / `SMC_M262` / `SMC_M580`).
   Uniqueness matters: sharing one `ConnectionID` across the three made EAE
   collide them and return **ReturnCode 101** on the duplicates.

2. **The Formatter + Publish are EMBEDDED inside each CAT**
   (`TemplateLibraryDeployer.PatchCatMqttPublish`): a `MqttFmt`
   (`MqttStateFormatter`) + `MqttPub` (`MQTT_PUBLISH`) inside the
   `Five_State_Actuator_CAT` / `Sensor_Bool_CAT`, bound to the connection on
   their own resource by matching `ConnectionID`. The embedded `MqttPub`
   carries `ConnectionID = config.MqttClientId` (`SMC_BX1`), so **BX1's** covers
   bind BX1's `MqttConn` and **publish** `smc/<comp>/state`.

3. **No cover changes, no bridge.** BX1's cover I/O still flows through the
   `BX1_IO` (`PLC_RW_BX1`) broker (`Bx1IoBrokerInjector` +
   `TemplateLibraryDeployer.EmbedCoverBridgeInComposite`) — that is the cover
   *control* path and is **independent of MQTT**. MQTT only *reports* state.

## Expected ReturnCodes on the rig

- **BX1** (Soft dPAC) runs MQTT → `MqttConn` connects (**RC 0**) and publishes its covers.
- **M262 / M580** dPAC firmware does **not** run an MQTT client → `MqttConn_M262`
  / `MqttConn_M580` read **RC 50** (firmware-gated). That is the **expected clean
  result** — it is NOT an error to "fix" with a bridge. Their per-resource
  connection exists only so this status is visible. **RC 101 = the shared-ID
  collision; the per-resource unique IDs are what avoid it.**

> M262/M580 cannot publish themselves (firmware). The user has accepted that
> trade-off: on the rig only BX1 publishes via MQTT. **Do NOT "solve" the missing
> M262/M580 telemetry by adding a bridge** — that is the rejected design.

## Files

- `CodeGen/CodeGen/Translation/SystemLayoutInjector.cs` — `InjectMqttConn` (three
  per-resource connections, unique `ConnectionID`/`ClientIdentifier`). **No `EmitBridge`.**
- `CodeGen/CodeGen/Services/TemplateLibraryDeployer.cs` — `PatchCatMqttPublish`
  (the embedded Formatter+Publish). **No `PatchCatExposeState` for the bridge.**
- `CodeGen/CodeGen/Devices/Core/SysresFbMirror.cs` — `BucketFor` routes
  `MqttConn`→BX1, `MqttConn_M262`→M262, `MqttConn_M580`→M580.
- `CodeGen/CodeGen/Services/MqttBridgeEmitter.cs` — **DELETED. Do not recreate.**

## Verification (must hold on every generated Demonstrator)

```
grep -c 'MQTT_CONNECTION'        <syslay>   # 3  (BX1 + M262 + M580)
grep -c 'Name="MqttConn_M262"'   <syslay>   # 1
grep -c 'Name="MqttConn_M580"'   <syslay>   # 1
grep -c 'Name="MqttFmt_'         <syslay>   # 0  (NO bridge pairs)
grep -c 'Name="MqttPub_'         <syslay>   # 0  (NO bridge pairs)
grep -c 'Name="FRAME_MqttBridge"' <syslay>  # 0  (no bridge frame)
```
