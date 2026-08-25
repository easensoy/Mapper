# EAE 24.1 HMI Generation & Harmony Deployment — Read-Only Investigation

**Date:** 2026-07-21 · **Status:** INVESTIGATION ONLY. No code written, no EAE solution edited/resaved/built/deployed, no controller or HMI touched. Archives were extracted to a temporary analysis directory only.

**Analysis copies (temp, never the originals):**
`…\scratchpad\hmi_analysis\main` (from `DemonstratorWithHMI.sln.zip`, 751 files) and `…\scratchpad\hmi_analysis\ts` (from `DemonstratorWithHMI_20260721-102309340.sln.zip`, 285 files).

**Evidence labelling:** **[F]** = verified fact read from a named file. **[I]** = inference with stated basis. **[U]** = unknown / requires workshop evidence.

---

## 1. Executive Conclusion

**Does the current Mapper generate a complete HMI? — No.** It generates the *control-plane* half and copies the *faceplate library*, but produces none of the four things that make an EAE HMI runnable: application screens, project registration of any canvas, a canvas/resolution topology, or an HMI runtime device. **[F]**

**Does `C:\Demonstrator` contain a deployable HMI? — No.** It contains a *valid, correctly-registered, compilable but empty* HMI project. Precisely: **[F]**
- `HMI\HMI.csproj` exists and **is** registered in `Demonstrator.sln` (`{5C9CB6C4-057C-4101-A72E-3087325B20F6}`, type `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}`, full Debug/Release `|Windows` entries). Hypotheses "no HMI project" and "not registered" are **both disproved**.
- But that csproj declares **3 `<Compile>` items** (`AssemblyInfo.cs`, `Colors\ProjectColors.cs`, `Colors\ProjectDrawingObjects.cs`), **0 `.cnv.cs` references and 0 `.def.cs` references** (reference: 47 and 3). The **9 CAT faceplate folders on disk are orphaned** and never compile into `HMI.dll`.
- **0 application screens.** `CanvasesResolutionList.xml` (769 B) contains only the `"Without resolution"` node with `StartCanvasClass=""` and `<Canvases />` **empty** — no resolution profile, no start canvas, no navigation.
- **No `HMI_NET` device** anywhere in `IEC61499\System\**` → no deployment target exists in the model.

**What is the intended Harmony target?** Not "Harmony HMI" generically. **[F]** The HMI is a distinct logical device `IEC61499\System\…\a441cfb6-5523-4d2c-a152-aacecdcef78e.sysdev` — `Name="HMI" Type="HMI_NET" Namespace="SE.Standard"`, carrying **only** `StartCanvas = Resolution=1024x768;Topology=Default;FirstCanvas=MainScreen`. Topology binds it via `Topology\Equipment_Workstation_1.json` → `logicalDeviceId = a441cfb6…`, endpoint `NIC_1\eno1\IP Address` = **192.168.1.2**, `logicalPort 61999` / `logicalPortSecured 51443`, certificate CN **`HMI_Linux_1235`** (SAN IPv4 192.168.1.2). It is **not** HMIB1X (which has *no* host-level `RuntimeDEO` at all) and **not** HMIP6's own runtime slots (both `logicalDeviceId` all-zeros, Soft dPAC `imageVersion:""`, no enrolled certificate). **[I, high]** the physical box is most likely the HMIP6 Linux panel PC modelled a second time as a generic `Workstation_1`; 192.168.1.2 is duplicated across both objects. **[U]** MAC/hostname evidence is absent — see §12.

**Largest current gap:** the **screen + registration + canvas-topology + HMI-device layer**. Everything below it (faceplate library, `_HMI.fbt` SIFBs, adapters, `.cfg` binding manifests, instance IDs) already exists and is already generated or copied.

**Is the problem generation, binding, topology, deployment, or runtime trust? — A combination of three, and notably *not* the other two. [F]**
- **Generation:** yes — no screens, no csproj registration, `.cfg` hardcodes one symbol.
- **Topology:** yes — no `HMI_NET` device, no Workstation/HMIP6 equipment in `C:\Demonstrator` (5 equipment objects vs 7).
- **Deployment:** yes — consequently no deployment target, port, or certificate binding.
- **Binding: no.** The binding mechanism is sound and already satisfiable (§5).
- **Runtime trust / OPC UA: not the problem.** See the reframe below.

> ### The single most important correction to the brief
> **The EAE HMI does not use OPC UA at all.** **[F]** All **71 of 71 `<OPCUAVariable>` entries across all 14 `.opcua.xml` files are `Enabled="false"`**, and a full-tree grep finds **zero** occurrences of `opc.tcp`, `EndpointUrl`, `SecurityPolicy`, `NodeId`, `BrowseName`, `4840`, or an OPC UA namespace index in either reference solution. The three system-level `opcua.xml`/`opcuaclient.xml` files are empty self-UID stubs. No OPC UA server is published and no OPC UA client is configured.
>
> The HMI instead uses **EAE-internal generated bindings**: `<CAT>.cfg` nominates the HMI SIFB as `<HMIInterface Name="IThis" Usage="Private">`; reads arrive via `IHMIAccessorService.GetInt64Value(channelId, cookie, eventIndex, ordinal)` and writes leave via `IHMIAccessorOutput.FireEvent(index, values)`. A symbol binds to a component by **`TagName` = the application-layer syslay FB ID**.
>
> Therefore §7's brief ("how HMI tags communicate over OPC UA") must be answered as: *they do not*. Enabling OPC UA would be a **new architecture**, not an increment.

> ### The single most important feasibility finding
> **`TagName` is exactly what the Mapper already computes.** **[F]** `MainScreen.cnv.Designer.cs:80` sets `this.Feeder.TagName = "E0AEF2679BD52F88"`, which is verbatim the `<FB ID="E0AEF2679BD52F88" Name="Feeder" …>` in the `.syslay` — and the Mapper writes that ID today via `FBIdGenerator.GenerateFBId(ComponentID)`. Screen generation is therefore **mechanically achievable from data the Mapper already owns**, enabled by `<CATInstancesHaveIds>true</CATInstancesHaveIds>` and `<HMIProject>HMI</HMIProject>` in `IEC61499.dfbproj`.

**Ground-truth ruling (hypothesis 22):** use **`DemonstratorWithHMI.sln.zip` (main)**. The timestamped archive is *not* merely build-cache-stripped: beyond 442 `SnapshotCompiles` files it is missing the entire `HMI_NET` device, the `SoftDpac` device and the only `opcuaclient.xml`, `Equipment_HMIP6_1.json` + `Equipment_Workstation_1.json`, the whole `sInterlock` faceplate state, and ~1.4 MB of embedded graphics; its `.def.cs`/`.event.cs` are materially shorter. **[F]**

---

## 2. Reference HMI Architecture — every layer

### 2.1 Layer stack

```
Control.xml component
  └─ syslay <FB ID="E0AEF2679BD52F88" Name="Feeder" Type="Five_State_Actuator_CAT">   ← application instance
       └─ .sysres <FB ID="60AEF…" Mapping="E0AEF2679BD52F88">                          ← resource placement (M262_RES)
            └─ CAT composite  Five_State_Actuator_CAT.fbt
                 ├─ control FBs (ActuatorCore, InterlockManager, FaultHandling …)
                 └─ IThis : Five_State_Actuator_CAT_HMI            ← HMI SIFB, embedded in the CAT
                      ├─ _HMI.meta.xml     (0 bytes — unused)
                      ├─ _HMI.offline.xml  (writable-port list + event-selection UI state)
                      └─ _HMI.opcua.xml    (publication policy — ALL Enabled="false")
  └─ <CAT>.cfg   <HMIInterface Name="IThis"> + <Symbol Name="sDefault|sSetup|sFault(IsFaceplate)|…">   ← THE live link
       └─ HMI project
            ├─ <CAT>.def.cs      faceplate accessors + DoOpenFaceplate dispatch
            ├─ <CAT>.event.cs    IHMIAccessorService readers / FireEvent writers
            ├─ <CAT>_sX.cnv.xml  <Mapping> mirror of the SIFB   (symbols only, never faceplates)
            ├─ <CAT>_sX.cnv.{cs,Designer.cs,resx}
            └─ Screens  MainScreen/ActuatorsScreen/Manual/Setup/StartCanvas_2
                 └─ symbol instance .TagName = "E0AEF2679BD52F88"   ← binds back to the application instance
  └─ CanvasesResolutionList.xml  1024x768 · StartCanvasClass · Topology FirstCanvas
  └─ HMI_NET device a441cfb6…  StartCanvas=Resolution=1024x768;Topology=Default;FirstCanvas=MainScreen
       └─ Equipment_Workstation_1.json  eno1=192.168.1.2  ports 61999 / 51443  cert HMI_Linux_1235
```

### 2.2 Solution & project **[F]**

`main\DemonstratorWithHMI.sln` — SharpDevelop-flavoured VS2010 format, 7 projects, flat (no `NestedProjects`):
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "HMI", "HMI\HMI.csproj", "{75FD96E8-E935-47EE-8572-DA9966AF39C9}"
```
The HMI is the **only** project using the stock Microsoft C# type GUID; all others use proprietary EAE type GUIDs. EAE behaviour comes from `<ProjectType>HMI</ProjectType>` inside the csproj. Configurations are `Debug|Windows` / `Release|Windows` only.

`HMI\HMI.csproj` (15,274 B) key properties: `ProjectType=HMI`, `HMILibraries=HMIBaseSymbols:`, `Theme=DefaultLight:Default|DefaultLight`, `NxtVersion=24.1.0.0`, `CanvasLookAndFeel=Theme`, `TargetFramework=netstandard2.0`, **`EnableDefaultItems=False`** (every file must be listed explicitly), `GenerateAssemblyInfo=False`. 22 references incl. `NxtControl.GuiFramework`, `NxtControl.ComponentModel`, `HMIBaseSymbols`, and 18 versioned `SE.*`/`Standard.*.HMI` assemblies spanning **24.1.0.17 – 24.1.0.33** (not uniform — must be read from the installed library set, never hardcoded). Trailer imports `Sdk.props`/`Sdk.targets` **and** `$(SharpDevelopBinPath)\SharpDevelop.Build.CSharp.Standard.targets` → **this project cannot be built by plain `dotnet build`**.

Mandatory item pattern (screen):
```xml
<Compile Include="MainScreen.cnv.cs"><Canvas>true</Canvas></Compile>
<Compile Include="MainScreen.cnv.Designer.cs"><DependentUpon>MainScreen.cnv.cs</DependentUpon></Compile>
<EmbeddedResource Include="MainScreen.cnv.resx"><DependentUpon>MainScreen.cnv.cs</DependentUpon></EmbeddedResource>
```
`<Canvas>true</Canvas>` appears on the 6 work-area screens and **not** on `StartCanvas_2` nor on any CAT state canvas. `DependentUpon` is **filename-only, never path-qualified**, even for files in subfolders.

### 2.3 Screens vs CAT faceplates **[F]**

7 root screens, namespace `HMI.Main.Canvases` (`Main` = the IEC61499 `LibraryName`, *not* a constant). Six derive from `NxtControl.GuiFramework.HMICanvas` at 1024×698 (= `WorkAreaWidth/Height`); `StartCanvas_2` derives from `StartCanvas` at 1024×768 and hosts the chrome (`WorkAreaControl`, `Login`, `CurrentUser`, `LanguageSwitcher`, `RuntimeConnection`, `HMIDeployment`, `TopologyCurrentCanvas`, `LogState`).

A **faceplate instance placed on a screen**:
```csharp
this.Feeder = new HMI.Main.Symbols.Five_State_Actuator_CAT.sDefault();
this.Feeder.BeginInit();
this.Feeder.DesignMatrix = new NxtControl.Drawing.Matrix2D(1D,0D,0D,1D, 24D, 376D);
this.Feeder.Name = "Feeder";
this.Feeder.SecurityToken = ((uint)(4294967295u));
this.Feeder.TagName = "E0AEF2679BD52F88";
this.Feeder.EndInit();
```
Invariants: `BeginInit/EndInit` bracket CAT symbols (not plain shapes); position is an affine `Matrix2D`, not `Bounds`; `SecurityToken=0xFFFFFFFF`; z-order = `Shapes.AddRange` order. Navigation is `ChangeCanvasButton.CanvasName = "ActuatorsScreen"` — a **string** matched against `CanvasesResolutionList.xml`.

**Same instance, many views:** `Feeder`/`E0AEF…` appears on MainScreen as `.sDefault` and on SetupScreen as `.sSetup`; `Feed_Station` appears as `.sAutomatic` and `.sManual`. One TagName, several state canvases.

### 2.4 Symbol vs Faceplate — what `.cnv.xml` really means **[F]**

| CAT | canvas | `.cnv.xml`? | kind |
|---|---|---|---|
| Area_CAT | sDefault | yes | Symbol |
| Five_State | sDefault, sSetup | yes | Symbol |
| Five_State | **sFault, sInterlock** | **no** | **Faceplate** |
| Process1_Generic | sAutomatic, sManual | yes | Symbol |

Symbols are *placed* and need their own contract; **faceplates are never placed** — they are opened at runtime and inherit the connection through `faceplate.SetConnectionInfo(this.TagName, this.SymbolPath, this.ChannelId, GetType())` in `.def.cs`. `<CAT>.cfg` marks them `IsFaceplate="true"` and omits `.cnv.xml` from their `<DependentFiles>`. Faceplate opening is declared in the designer: `drawnButton1.OpenFaceplates.Add(new OpenFaceplate("sFault", MouseButtonType.Click))`, dispatched by the `if`-chain in `.def.cs`.

`.cnv.xml` is **per-CAT, not per-state** — the sDefault and sSetup copies are byte-identical.

### 2.5 Canvas topology, alarms, themes **[F]**

`CanvasesResolutionList.xml` carries **both** startup concepts: `StartCanvasClass="HMI.Main.Canvases.StartCanvas_2"` (the shell) and `<Topology Name="Default" FirstCanvas="MainScreen">` (the first work-area canvas), plus chrome flags (`Login`, `Logger`, `NavigationControl`, `RuntimeConnection`, `NavigationBar`, `ResizeBehaviour="Standard"`).

`Alarms\AlarmClasses.xml` is an **empty root** in all four trees; `SystemAlarmClasses.xml` holds only the framework `Alarm` (Prio 80) / `Warning` (Prio 60) classes. **No rig-specific alarm exists anywhere.** Fault semantics are instead hard-coded in `Five_State_Actuator_CAT_sFault.cnv.cs:41-53`: `1001`→"At Work Timeout", `1002`→"At Home Timeout", `1003`→"Sensor Fault", default→"Unknown Fault" — a hand-written bypass of the alarm subsystem.

`GraphicsList.xml`, `ImageStorage.xml`, `Configurations\*`, `Languages\*` are empty stubs in **all four** trees (so they are non-issues). `Colors\ProjectColors.xml` has 7 overrides which EAE merges into the 134 KB `ProjectColors.cs` + 63 KB `ProjectDrawingObjects.cs`.

---

## 3. Project Comparison Matrix

A = reference main · B = reference timestamped · C = SMC_Rig_Expo_withClamp_RevPi · D = `C:\Demonstrator` (Mapper output)

| Artifact | A | B | C | D |
|---|---|---|---|---|
| Total files | 751 | 285 | 605 | 456 (358 excl. bin/obj) |
| `HMI\HMI.csproj` | 15,274 B | 14,886 B | 22,737 B | **8,215 B** |
| HMI registered in `.sln` | `{75FD96E8…}` | `{75FD96E8…}` | `{10FB5432…}` | **`{5C9CB6C4…}` ✔** |
| csproj `<Compile>` items | 37 | 35 | 71 | **3** |
| csproj `.cnv.cs` refs | 47 | 44 | 70 | **0** |
| **Root screens** | **7** | 7 | 2 | **0** |
| Root `.cnv.Designer.cs` / `.resx` | 7 / 7 | 7 / 7 | 2 / 2 | **0 / 0** |
| `.def.cs` / `.event.cs` / `.Design.resx` | 3/3/3 | 3/3/3 | 16/16/16 | 9/9/9 |
| CAT faceplate folders | 3 | 3 | 16 | **9** |
| `CanvasesResolutionList.xml` | **populated** | populated | populated | **STUB (empty `<Canvases/>`)** |
| `GraphicsList` / `AlarmClasses` / `ImageStorage` / `Configurations` | stub | stub | stub | stub |
| `*_HMI.fbt` | 8 | 8 | 16 | 8 |
| `*_HMI.meta/.offline/.opcua.xml` | 7/7/7 | 7/7/7 | 13/14/14 | 7/7/7 |
| `*HMIAdptr.adp` | 2 | 2 | 2 | 2 |
| System `opcua.xml` | 2 | 1 | 6 | 7 (all 251 B stubs, placeholder GUIDs) |
| System `opcuaclient.xml` | 1 (dead stub) | 0 | 6 | **0** |
| **`HMI_NET` device** | **yes `a441cfb6…`** | **no** | **yes `4d3e48d3…`** | **NO** |
| Topology `Equipment_*` | 7 | 5 | 8 | **5** |
| HMIB1X / HMIP6 / RevPi / Workstation | y/y/n/y | y/n/n/n | y/y/**y**/y | y/**n**/n/**n** |
| `General\Certificates\Certificates.xml` | 11,979 B | 10,569 B | 19,637 B | 13,628 B |
| `bin`/`obj`/`RuntimeData`/`Deploy` | no | no | no | **yes** (HMI.dll 90,112 B built) |

**What D lacks relative to A:** all 7 screens (21 files); all canvas/faceplate csproj registration; a populated `CanvasesResolutionList`; the `HMI_NET` device; `opcuaclient.xml`; the `Workstation_1` + `HMIP6_1` topology equipment; real device GUIDs on system `opcua.xml`; and the `sFault`/`sInterlock`/`sSetup` faceplate states (D ships a stale lowercase `_setup` variant with no `.cnv.xml`).

---

## 4. Mapper Generation Coverage

| Required HMI artifact | Status | Evidence |
|---|---|---|
| HMI FB instances (`Area_HMI`, `Station1/2_HMI`) + adapter wiring | **Generated correctly** | `SystemLayoutInjector.cs:951-952, 968-980, 2328-2332`; `ResourceWireEmitter.cs:68-74` |
| `_HMI.fbt`, `_CAT.opcua.xml`, `_HMI.opcua.xml`, faceplate `.def.cs`/`.event.cs`/`.cnv.*` | **Copied from template** (copy-if-absent) | `TemplateArtifactDeployer.ExtractToEae:186`; roots `{IEC61499, HMI, HwConfiguration}` at `:161` |
| `<CAT>.cfg` binding manifest | **Generated incompletely** — hardcodes a single `<Symbol Name="sDefault">`, and is itself copy-if-absent (`:205`) | `TemplateArtifactDeployer.cs:198-236` |
| `<CAT>_HMI.meta.xml` | Generated as **0-byte placeholder** | `TemplateArtifactDeployer.cs:229-233` |
| Per-container `opcua.xml` | **Generated as schema stub only** (UID = folder GUID; no variables, no NodeIds) | `OpcuaCompanionEmitter.cs:26, 88-95` |
| `HMI.csproj` registration of canvases | **Missing** — 0 references in any `.cs` | grep: `HMI.csproj` has zero source hits |
| Application screens (`.cnv.cs`/`.Designer.cs`/`.resx`) | **Missing** | none generated; 0 present in D |
| `CanvasesResolutionList.xml` (resolution, start canvas, topology) | **Missing** | 0 source hits |
| `GraphicsList.xml`, `AlarmClasses.xml` | **Missing** (but stubs in all references — low priority) | 0 source hits |
| `opcuaclient.xml` | **Missing** — only pattern-matched for deletion | `DfbprojRegistrar.cs:383` |
| `HMI_NET` device + `StartCanvas` property | **Missing** | no `HMI_NET` in source |
| Harmony deployment target / ports / certificate | **Missing** — **zero `Harmony` hits in any `.cs`** | — |
| `.sln` authoring | **Should not be generated** — Mapper never writes one; it mutates an existing tree and aborts if the syslay is absent | `MainForm.cs:468-473, 971` |
| `ProjectColors.cs` / `ProjectDrawingObjects.cs` (134 KB/63 KB) | **Should not be generated** — proprietary theme merge | reference only |
| `.cnv.resx` carrying `ImageBytes` (one is 1,409,531 B) | **Should not be generated** — copy verbatim | `Five_State_Actuator_CAT_sDefault.cnv.resx` |
| Mode/CycleType/FaultReset command surface | **Unclear→absent**; the Mapper actively **removes** the mode gate | `ProcessRuntimeTemplatePatcher.cs:407-445` deletes the `Mode=1 AND CycleType<>0` bypass; `FaultReset` = 0 hits |

---

## 5. End-to-End Binding Examples

Canonical hop chain: `entity → syslay FB (ID) → .sysres (Mapping) → CAT internal FB → IThis.<port> → [adapter, Area/Station only] → _HMI.fbt VarDeclaration(UID) → .opcua.xml policy (disabled) → <CAT>.cfg → .cnv.xml → .event.cs accessor → canvas control (TagName = port name)`.

**(a) Status — Five_State `current_state_to_process` (READ).** `syslay:122` `<FB ID="E0AEF2679BD52F88" Name="Feeder">` → `1459BCD12760907D.sysres:17` `ID="60AEF…" Mapping="E0AEF…"` (**M262/M262_RES**) → `Five_State_Actuator_CAT.fbt:671` `ActuatorCore.current_state_to_process → IThis.current_state_to_process`, event `FB2.EO2 → IThis.pst_event` → `_HMI.fbt:57` `ID="5A0130953765A4BF" Type="INT"` → `_HMI.opcua.xml:19-26` `Enabled="false"`, `AccessLevel=1 (CurrentRead) Locked="true"`, `RTAddress=V1;${VariableFullPath}` → `.cnv.xml` `<Event Name="pst_event">current_state_to_process</Event>` → `.event.cs:89-107` `GetInt64Value(channelId, cookie, eventIndex, true, 0, …)` → `sDefault.cnv.Designer.cs:102` `FreeText<short>` `TagName="current_state_to_process"`, `IsOnlyInput=true`, five `Range<short>` colour bands. **Direction: READ only.**

*Classification note:* this is a **display state** (ActuatorCore's computed 0..4), distinct from the **process values** `atHome`/`atWork` (from `InputHandler`) and from the **command echo** `toWorkPLC`/`toHomePLC` (from `ActuatorCore.outputToWork/Home`). Three concepts, three ports — correctly separated.

**(b) Fault (READ).** `FaultHandling.fault_active/fault_code → IThis`, event `FB7.EO1 → IThis.FAULT_EVENT` → `_HMI.fbt:60-61` (`251FA560E07D0DDB` BOOL, `9ADDF62C69234092` INT) → both `Enabled="false"` → `.event.cs:169-224` → `sDefault.cnv.cs:32-39` shows `fault_code` only when `fault_active` → faceplate `sFault` decodes 1001/1002/1003. **No fault-reset write path exists on the actuator** — reset lives only at Area/Station (`FRCNF`).

**(c) Interlock (READ).** `InterlockManager.{Work1Interlock, HomeInterlock, MoveAllowed, ActiveRuleIndex, ActiveSourceID, ActiveBlockedState} → IThis`, event `CNF_WORK1 → interlock_event` → `_HMI.fbt:64-69` → all `Enabled="false"` → `sInterlock` faceplate renders blocked-movement / current-state / reason / required-action. **Gap [F]:** `MoveAllowed`, `ActiveRuleIndex`, `ActiveSourceID` reach the faceplate's event args but are **never displayed**. No interlock bypass/override write exists anywhere — correct.

**(d) Mode — the only closed-loop operator command.**
*Down (request):* `Area_CAT_sDefault.cnv.cs:36-88` `FireEvent_MCNF(1|2|3|9)` → `.event.cs:312` `FireEvent(0, new object[]{Mode})` → `.cnv.xml` `<Event Name="MCNF">Mode</Event>` → `_HMI.fbt:50` `ID="38488A1E01A33DBB"` → `.opcua.xml:39-45` **no AccessLevel, no Exposed**, `RTAddress=…;Trigger=MCNF` → `Area_CAT.fbt:33,53` `IThis.MCNF/Mode → AreaHMIAdptrOUT` → `syslay:395` `Area_HMI.AreaHMIAdptrOUT → Area.AreaHMIAdptrIN` → `Area.fbt:90` `AreaHMIAdptrIN.Mode → AreaAdptrOUT.ModeCMD`. Host: **M262/M262_RES**.
*Up (actual mode):* `Area.fbt:71,102` `AreaAdptrOUT.LLM/LL_Mode → AreaHMIAdptrIN.SMREQ/SystemMode` → `IThis.MSTS/System_Mode` → `Area_CAT_sDefault.cnv.cs:183-211` `int mode = e.System_Mode.Value` drives button visibility.
Enums confirmed in code: Mode `1`=Auto `2`=Manual `3`=Setup `9`=Initial Position; CycleType `0`=Stop `1`=Continuous `2`=StopAtEndOfCycle `3`=Single.
**Dead ends [F]:** `Fault_Reset`/`FRCNF` and `LL_Fault_Status`/`LLFSTS` are wired end-to-end through adapter and `Area.fbt:65` but **no canvas code fires or reads them**.

**(e) Process step + manual execute.** `TSString/NSString/PSString.OUT1 → IThis.ThisStepText/…`, event `SCNF` → displayed `IsOnlyInput=true`. Command: `Process1_Generic_sManual.cnv.cs:27-36` sets `chkManualExecuteStep.Value=true; FireEvent_MREQO(true)` on MouseDown and `false` on MouseUp (**momentary/pulse**) → `.event.cs:646` `FireEvent(0,…)` → `_HMI.fbt:74` `ManualExecuteStep` `Trigger=MREQO` → `Process1_Generic.fbt:171` `IThis.MREQO → ProcessEngine.MREQ`. Acknowledgement: `ProcessEngine.ManualStepReady/ManualStepComplete → IThis → display`. **Clean three-phase handshake: ready (permissive) → execute (command) → complete (ack).**

**(f) Actuator jog — mode-gated.** `sSetup.cnv.Designer.cs:329,345` bind `toHome`/`toWork` **without** `IsOnlyInput` (the only writable actuator controls in the project) → `cmd_event` → **`Five_State_Actuator_CAT.fbt:461` routes `IThis.cmd_event → ActuatorCore.setup_event`**, i.e. the jog is consumed on the Setup-mode input and only acts in Setup. The `sDefault` canvas exposes no writable control at all.

---

## 6. HMI Artifact Dependency Graph

```
Control CAT (.fbt)  ──contains──►  IThis : <CAT>_HMI.fbt   ──sidecars──►  _HMI.meta.xml   [0 bytes, unused]
      │                                    │                              _HMI.offline.xml[writable-port list → feeds Trigger=]
      │                                    │                              _HMI.opcua.xml  [publication policy, ALL disabled]
      │                                    └──(Area/Station only)──► AreaHMIAdptr / StationHMIAdptr (.adp, PLUG/SOCKET)
      │
      └──registered by──►  <CAT>.cfg  ◄── THE ONLY LIVE LINK TO THE HMI PROJECT
                              │  SymbolDefFile → HMI\<CAT>\<CAT>.def.cs
                              │  SymbolEventFile → HMI\<CAT>\<CAT>.event.cs
                              │  DesignFile → HMI\<CAT>\<CAT>.Design.resx
                              └─ <HMIInterface Name="IThis">
                                   <Symbol Name="sDefault">  + DependentFiles(.Designer.cs,.resx,.cnv.xml)
                                   <Symbol Name="sFault" IsFaceplate="true">  (no .cnv.xml)
                                        │
                                        ▼
                            HMI project (HMI.csproj, EnableDefaultItems=False)
                                   │  screens: <Screen>.cnv.cs + .Designer.cs + .resx
                                   │     └─ symbol instance .TagName = syslay FB ID  ──────┐
                                   └─ CanvasesResolutionList.xml (1024x768, StartCanvasClass, FirstCanvas)
                                        │                                                  │
                                        ▼                                                  │
                            HMI_NET device a441cfb6… (StartCanvas property)                │
                                        │                                                  │
                            Equipment_Workstation_1.json  eno1=192.168.1.2  61999/51443    │
                                        │                                                  │
                                   ┌────┴──────────── EAE HMI channel (channelId/cookie) ──┘
                                   ▼
              M262 192.168.1.10   ·   M580 192.168.1.20   ·   BX1 192.168.1.151
              (the *_HMI FB halves are compiled INTO the controllers)
```

**Key structural facts [F]:** the `HMI_NET` device has `<FBNetwork/>` and **no `.sysres` at all** — it hosts zero function blocks. Every `*_HMI` SIFB is deployed **on the controllers** (`Area_HMI`, `Station1_HMI` → M262; `Station2_HMI` → M580; the rest embedded in each CAT wherever it runs). Two HMI attachment patterns coexist: **Pattern A** (Area/Station) puts the SIFB in a separate wrapper CAT coupled by adapter — detachable; **Pattern B** (everything else, newer) embeds `IThis` directly inside the control CAT — inseparable, no adapter. There are **no HMI adapters for any other CAT type**.

---

## 7. Harmony Deployment-Target Analysis

**Every entity kept distinct — do not conflate. [F]**

| Entity | Address | Evidence |
|---|---|---|
| BX1 Soft dPAC **container** (`HMIB1X_1\Softdpac_1`, `HMIB1X_SoftdpacContainer_V01.00_01.00`) | **192.168.1.151** | `Equipment_HMIB1X_1.json:41`; `RuntimeDEO.logicalDeviceId=…0004`; scannerId `270AFDB7F209BFE8`; docker `softdpac v24.1.25090.08` |
| BX1 / HMIB1X **host box** (`HMIB1X_V01.00_01.00`) | **192.168.1.209** | `Equipment_HMIB1X_1.json:109`; cert SAN. **Has NO host-level `RuntimeDEO`** — only `SoftdpacManagerDEO` on **port 8080** |
| HMIP6 **host box** (`HMIP6_V01.00_01.00`) | **192.168.1.2** (`eno1`) | `Equipment_HMIP6_1.json:109`. Host `RuntimeDEO` exists with **`logicalDeviceId` all-zeros = UNASSIGNED**, ports 61999/51443 |
| HMIP6 Soft dPAC **container** | **192.168.1.1** | `Equipment_HMIP6_1.json:45`; `imageVersion:""` (**never provisioned**), `logicalDeviceId` all-zeros |
| **EAE HMI runtime (the target)** | **192.168.1.2 : 61999 / 51443 secured** | `Equipment_Workstation_1.json` `logicalDeviceId=a441cfb6…`, endpoint `NIC_1\eno1\IP Address` |
| OPC UA server address | **NONE EXISTS** | zero `opc.tcp`/`4840`/endpoint strings in either solution |

**Runtime type:** `HMI_NET` (namespace `SE.Standard`) — *not* `Soft_dPAC`, *not* AVEVA OMI (the `AvevaOMI.omiproj` is an empty stub in every tree). **Display resolution:** carried only on the logical device — `StartCanvas = Resolution=1024x768;Topology=Default;FirstCanvas=MainScreen` — never on a topology object.

**Certificates [F]:** a single flat `General\Certificates\Certificates.xml` (8 entries keyed by `RuntimeDEO.uuid`; **no PKI/trust-list folder structure, no CRL**). The HMI's is `Key="0a062e36-bf86-4578-83af-e7ea841f23ab"`, **CN=`HMI_Linux_1235`**, SAN IPv4 **192.168.1.2**, `urn:SchneiderElectric:EcoRT`, valid 2025-01-01→2030-01-01. All certs are **EcoRT TLS deployment identities — none is an OPC UA application instance certificate.** No certificate exists for HMIP6's own runtime slots, corroborating that they were never used.

**Verdict [I, high confidence]:** the HMI deploys to the logical `HMI_NET` device carried by `Workstation_1` at **192.168.1.2**. Because `Workstation_1\NIC_1\eno1` and `HMIP6_1\eno1` are **the same IP**, and the certificate says *Linux* HMI, the physical box is almost certainly the **HMIP6 Harmony panel PC**, modelled twice — once correctly as `HMIP6_1` (whose `Runtime_1` slot, with identical `typeId 422ee926…` and identical 61999/51443 ports, sits **empty**) and once as a generic `Workstation_1` where the HMI device was actually bound. Deployment lands on 192.168.1.2 either way; the model is inconsistent.

**[U] To settle it conclusively, the workshop must provide:** the MAC/`physicalAddress` of the box answering on 192.168.1.2 (all `physicalAddress` fields are empty strings in both solutions), or its hostname/serial, or the EAE Runtime Manager connection history / deployment log. None is in the solution tree.

**Security note [F]:** `F513CAE3-…Properties.xml` sets `Configuration/SecurityApp/InsecureApplication/Enable = True` on **M262, M580 and BX1** — the controllers accept unauthenticated application deployment. The HMI device's property file contains only `StartCanvas` (different device class, no security group).

---

## 8. Distributed Communications Analysis

**How the HMI obtains a coherent plant view: option (d) — EAE-internal generated bindings. [F]**

One HMI runtime holds a **single flat tag namespace** whose entries are application FB instance IDs spanning all three controllers:

| `TagName` | object | resource | device |
|---|---|---|---|
| `86119B275B332F9D` / `F8DBD8A29CB500EC` / `E0AEF2679BD52F88` / `7C837DE5770A8EEE` / `4056993A201F2282` | Area_HMI, Feed_Station, Feeder, Checker, Transfer | `1459BCD12760907D` M262_RES | **M262 192.168.1.10** |
| `A219BC2DC64B6DFE` / `DA6F878A52416E4E` / `ACB6DD22361664F5` / `EC87D797727FF5F4` / … | Assembly_Station, Disassembly, Clamp, Shaft_* | `3E5C2B7F1A4D6C8E` RES0 | **M580 192.168.1.20** |
| `10C5485ED8865854` / `2C5F84BA3C43A064` / `CCCF3FF0DF5DF211` | CoverPNP_Hr/Vr, CoverPnp_Gripper | `78E9CD3D27851B64` BX1_RES | **BX1 192.168.1.151** |

Options **(a) HMI-to-every-OPC-UA-server**, **(b) Soft dPAC aggregator**, and **(c) CrossComm consolidates for the HMI** are all **ruled out**: 71/71 OPC UA variables disabled and no endpoint exists; the only `opcuaclient.xml` belongs to the orphan `SoftDpac` device (`8e5bdb40…`) which has an **empty `<FBNetwork/>`**, no topology equipment, no IP, and compiles only under the `(Local Test)` profile — it is auto-generated boilerplate, not a configured client (my earlier inference to the contrary is **corrected**).

**What CrossComm actually does [F]** — controller↔controller only, generated at compile time (`nxtv3` over UDP, `alive=3000`, `besteffortonly=true`), in a **chain M262 ↔ M580 ↔ BX1** with no direct M262↔BX1 link:
```
M262.M262_RES :51300  ↔  M580.RES0 @192.168.1.20:51302   (Disassembly→Ejector, PartAtAssembly→BearingSensor)
M580.RES0     :51301  ↔  BX1.BX1_RES @192.168.1.151:51303 (Clamp→CoverPNP_Hr, CoverPnp_Gripper→Assembly_Station)
```
**Neither the HMI nor SoftDpac appears in any CrossComm channel.** MQTT (`mqtt://192.168.1.50:1883`, three Telemetry FBs) is a third, out-of-band path.

**Port map [F]:** 61999 = HMI deployment; 51443 = HMI secured deployment (present **only** on the two HMI-capable RuntimeDEOs, *not* on the controllers); 8080 = Soft dPAC Manager on each host box; 51300–51303 = CrossComm data; 41500–41502 = `ReliableCrossComm` bind URIs; 51496–51503 = simulation deploy/archive; 1883 = MQTT; **4840/48400+ = absent everywhere**.

**Revolution Pi — the addressing disambiguation you asked for [F]:**
- **`192.168.1.1` is the HMIP6 Soft dPAC container in BOTH solutions. It is never the RevPi.**
- RevPi Soft dPAC container = **192.168.1.6** (configured in `Equipment_Revolution Pi.json` → `Softdpac_3.Eth0`, and confirmed in the compiled `commdesc.xml`: `nxtv3://192.168.1.6:51300` / `:51301`).
- RevPi **host** = 192.168.1.2 (`NIC_2\eth0`), whose enrolment cert has SANs `172.18.0.2`, `192.168.1.2`, `127.0.0.1`.
- RevPi is modelled as a **generic `Workstation_V01.00_01.00`** with a generic `SoftdpacContainer_V01.00_01.00` (no RevPi catalog entry), logical device `72a1fde3…` `Name="Revolution_Pi" Type="Soft_dPAC"`, resource `D090B4163A62A815`, I/O via Modbus TCP to `172.18.0.1:502` (the Docker bridge gateway = the RevPi host). **The RevPi host has no `RuntimeDEO` → it does not host an HMI runtime.**
- ⚠ `Topology\Content\49d2ea8e-…_IOProfile.xml` contains a stale DTM default `192.168.1.1`; the authoritative TM3BC address is **192.168.1.210** (topology JSON + compiled `EIPSCANNER2.xml`). Do not read that as the HMIP6 container.

**What adding RevPi requires on the HMI side:**

| Change | Needed? | Why |
|---|---|---|
| New OPC UA data source | **No** | No OPC UA in use; HMI binds by `TagName`→Mapping ID |
| New certificate trust | **No manual step** | Certs auto-enrol per `RuntimeDEO.uuid` into `Certificates.xml` |
| New NodeIds | **No** | None exist |
| New CrossComm | **Only if** RevPi FBs interlock with M262/M580/BX1 FBs — auto-generated from cross-device `FBConnection`s; not needed for the HMI to see RevPi data |
| Topology + logical device | **Yes — the bulk** | New Workstation + NIC_EAE + SoftdpacContainer with a **non-colliding** IP (`.6` is free in the main solution), plus a `Soft_dPAC` logical device bound via `logicalDeviceId` |
| Application + HMI | **Yes** | `PLC_RW_REVPI` + Modbus symlinks + HW config; place CAT instances; drop symbols on a canvas with `TagName` = new Mapping IDs |
| Possible shortcut | | The orphan `8e5bdb40…` "SoftDpac" device in the main solution occupies **exactly** the RevPi slot (identical Deploy 51503 / Archive 51502 / CrossComm `udp://0.0.0.0:41502`) — **[I]** very likely a half-started RevPi integration. Confirm before creating a new device. |

**The HMI must not silently keep pointing at `.1`** — `.1` is HMIP6's *unprovisioned* Soft dPAC container, not the RevPi and not the HMI.

---

## 9. Missing Data-Model Semantics

The Mapper's inputs (Control.xml, `SMC_Rig_IO_Bindings.xlsx`, `Config\*.yml`, `mapper_config.json`) contain enough to infer the control-side facts but **carry no presentation semantics at all**.

**Safely inferable today [F]:** faceplate type from CAT type (`TemplateMap.ResolveActuatorCatType`); controller ownership from `ComponentRegistry`/`ControllerMap`; read/write direction from the `_HMI.fbt` interface (InputVar = read, OutputVar+`Trigger=` = write); instance identity (`TagName`) from `FBIdGenerator`; station/area grouping from `ComponentRegistry.ProcessOwner`; a *coarse* screen position from `LayoutGrid` (column/row) — though that is a syslay diagram grid, not an HMI layout.

**Requires explicit engineering input (absent today):** screen membership & grouping; canvas resolution & work-area size; per-instance X/Y (the `Matrix2D` translation); display label & tooltip; preferred faceplate variant per screen (`sDefault` vs `sSetup` vs `sAutomatic`/`sManual`); navigation graph & button placement; start canvas; operator roles/permissions (`SecurityToken`); confirmation prompts; alarm class, severity, message text and recovery guidance; interlock wording; engineering units, ranges, decimal precision, formatting; visibility/enable conditions; colour/status rules beyond the CAT's built-in `Range<T>` bands; and the HMI deployment target (host, IP, ports, resolution).

**Also missing structurally:** state *names* (Control.xml has them; the HMI shows raw ints and hardcodes text in `.cnv.cs`) — the same gap raised in the 2026-07-21 technical meeting; and a canonical Mode/CycleType enum in the generator (values exist only as literals in `Area_CAT_sDefault.cnv.cs`).

---

## 10. Recommended Data-Driven HMI Schema (described, **not** implemented)

A single generated `hmi-model.json`, sibling to the existing `sync-map.json`/manifest concept, keyed on data the Mapper already owns:

- **`deployment`** — `{ device: { name:"HMI", type:"HMI_NET", namespace:"SE.Standard", uid:<stable> }, host: { equipment:"Workstation_1"|"HMIP6_1", nic:"NIC_1\\eno1", ip, port:61999, portSecured:51443 }, resolution:{ w:1024, h:768, workArea:{w:1024,h:698} }, startCanvasClass, firstCanvas }`
- **`screens[]`** — `{ id, title, kind: "work"|"start", canvasClass, members[] }`
- **`members[]`** — `{ componentRef, catType, faceplateVariant:"sDefault|sSetup|sAutomatic|sManual", x, y, label, securityRole }` (`x`,`y` become the `Matrix2D` translation; `componentRef` resolves to `TagName`)
- **`hierarchy`** — area → station → process → component (already derivable from `ComponentRegistry`)
- **`faceplates[]`** — per CAT: `{ catType, symbols:[{name, hasContract:true}], faceplates:[{name, openTrigger:"Click", fromSymbols:[…]}] }` → drives both `<CAT>.cfg` and `.def.cs`
- **`bindings[]`** — per CAT port: `{ port, dir:"read"|"write", type, event, ordinal, trigger }` → derived 1:1 from `_HMI.fbt`; drives `.cnv.xml` and `.event.cs`
- **`states{}`** — `{ catType: { 0:"At Home Initial", 1:"Moving to Work", … } }` from Control.xml — removes the hardcoded switch statements
- **`alarms[]`** — `{ code, class:"Alarm|Warning", priority, message, cause, recovery }` (e.g. 1001/1002/1003) → drives `AlarmClasses.xml` and the fault faceplate text
- **`interlocks[]`** — `{ target, from, to, sourceId, blockedState, explain }` → the interlock faceplate's reason/required-action text
- **`navigation[]`** — `{ from, to, buttonLabel, x, y, visibleWhenMode[] }`
- **`roles[]`** / **`formatting{}`** — permissions and units/precision per port

Everything except `screens[].members[].x/y`, labels, alarm wording, navigation and roles is **derivable**; those are the genuinely new engineering inputs.

---

## 11. Stable-Identity Strategy

| Identity | Must be | Source of truth | Today |
|---|---|---|---|
| Solution/project GUIDs (`{75FD96E8…}` etc.) | Stable forever | The EAE project tree | Pre-existing; Mapper never writes a `.sln` — **correct** |
| CAT/type UIDs, `_HMI.fbt` var UIDs (`5A0130953765A4BF`) | Stable per type; copied from the library | Template Library CAT zips | **Correct** (copied verbatim) |
| **Application FB instance ID (= `TagName`)** | **Deterministic, stable across runs, unique per instance** | Must become a persisted ledger | `SHA256(ComponentID)[0..8]` — deterministic but **seed-fragile** |
| `.sysres` mirror ID | Derived from the syslay ID | — | top-nibble XOR-8; **already caused duplicate-instance bugs** (`SysresFbMirror.cs:179-181`) |
| Counter-allocated FB IDs (broker/patcher) | Order-independent | — | `IDCounter`/`max+1` — **order-dependent** |
| OPC UA NodeIds | n/a while OPC UA is off | EAE at build | **none exist** (`grep NodeId` = 0 hits) |
| Certificates | Auto-enrolled per `RuntimeDEO.uuid` | EAE Runtime Manager | Not Mapper's concern — **never copy between projects** |
| Alarm identifiers | Stable per code | The new schema | Hardcoded in `.cnv.cs` |

**The core risk [F]:** `FBIdGenerator.GenerateFBId(seed)` = `SHA256(seed)[0..8]` where the seed is a **component name or `ComponentID`**. Rename a component in the twin, or recreate it, and the ID changes wholesale. Because `TagName` *is* that ID, **every screen binding would break on a rename** — the same class of failure as the existing `.hcf` `{resId}.{fbId}.{port}` split-brain that `HcfReferenceValidator` exists to catch. Compounding it, `MainForm.cs:971` deep-cleans `C:\Demonstrator` before every Generate, so anything EAE stored about an instance and not re-derivable is lost.

**Consequences if IDs churn:** dangling `TagName` → "Found References to Missing Instances"/"Repair Instances"; dangling `.hcf` symlinks; stale `obj/System.hash`/`.obsolete` caches → Solution Integrity errors; loss of online-change compatibility; alarm-history discontinuity; the runtime treating the project as new.

**Recommendation (conceptual):** introduce a **persisted identity ledger** (`hmi-identity.json`) mapping `ComponentID → FB ID`, written once and thereafter *read* — new components get new IDs, existing ones keep theirs across renames. Type-keyed artefacts (CAT names, `_HMI.fbt`, adapter names, `.cnv.xml`) are already stable and need no ledger.

---

## 12. Generation-Strategy Recommendation

| # | Strategy | Reliability | Maintainability | Automation fit | Verdict |
|---|---|---|---|---|---|
| 1 | **Fully native generation** of every HMI file | Low — must reproduce `ImageBytes` resx (1.4 MB), the 134 KB/63 KB theme-merged `.cs`, and undocumented serializer invariants | Low | High | **Reject** for resx/theme; viable for the deterministic subset |
| 2 | Declarative intermediate + supported EAE import | **[U]** no such import path found in the tree or tutorials | — | — | **Unknown** — requires Schneider confirmation |
| 3 | **Clone validated CAT faceplate templates & instantiate** | High — already how the Mapper works | High | High | **Already in place**; needs `.cfg` completion |
| 4 | **Hybrid: maintained baseline HMI shell + generated screens/instances/bindings/registration** | **High** | **High** | **High** | ✅ **RECOMMENDED** |
| 5 | EAE automation API | **[U]** none found; tutorial workflow is GUI drag-drop | — | — | Investigate separately |
| 6 | Generate controller-side only, hand-draw HMI | High | **Low** — the drift trap already visible | Low | Reject as the end state |

**Recommended: strategy 4 (hybrid).** Keep a version-controlled baseline HMI shell (themes, `Colors\*.cs`, `StartCanvas_2` + its `logo.ImageBytes` resx, `Configurations`, `Languages`, framework alarm classes) and have the Mapper generate only the deterministic layer:

**Safe to generate [F, per file-level analysis]:** `<Screen>.cnv.cs` (28-line template) · `<Screen>.cnv.Designer.cs` (**the main target** — no opaque data) · `<CAT>_sX.cnv.xml` (1:1 from `_HMI.fbt`) · `<CAT>.event.cs` (deterministic: event index, `With` ordinal, type map) · `<CAT>.def.cs` (cartesian symbols × faceplates from `.cfg`) · `<CAT>.Design.resx` (constant empty ResX) · `CanvasesResolutionList.xml` · `AlarmClasses.xml` · **`<CAT>.cfg` (mandatory — nothing works without it)** · `HMI.csproj` item groups.

**Never generate — copy or leave to EAE:** any `.cnv.resx` containing `ImageBytes` · `Colors\ProjectColors.cs` / `ProjectDrawingObjects.cs` · hand-authored `.cnv.cs` faceplate logic (generate only the empty-constructor skeleton, never overwrite) · the `.sln` · certificates.

**Serializer invariants a generator must honour [F]:** `BeginInit/EndInit` on CAT symbols only · `Matrix2D` for symbols vs `Bounds`/`Location` for plain shapes · `SecurityToken=0xFFFFFFFF` · `Shapes.AddRange` z-order = declaration order · `SymbolSize` (symbols) vs `Size` (canvases/faceplates) · `((float)(1024D))` double-cast idiom · `DependentUpon` filename-only · `.def.cs`+`.event.cs` are an atomic pair · lower-case `<none Include=…>` and the missing trailing `\` on Release `OutputPath` (EAE fingerprints; reproduce or accept diff noise).

---

## 13. HMI Deployment Decision Tree

```
1. Does IEC61499.dfbproj declare <HMIProject>HMI</HMIProject> and <CATInstancesHaveIds>true</CATInstancesHaveIds>?
     NO → TagName binding impossible.                                     [D: verify]
2. Is HMI.csproj registered in the .sln with type {FAE04EC0-…}?
     C:\Demonstrator: PASS ✔
3. Does HMI.csproj list every canvas (<Compile Canvas=true> + Designer + EmbeddedResource)?
     C:\Demonstrator: FAIL — 0 canvas refs.  → nothing compiles into HMI.dll
4. Do application screens exist?
     C:\Demonstrator: FAIL — 0 screens.
5. Does CanvasesResolutionList.xml define a real resolution + StartCanvasClass + FirstCanvas + <Canvas> list?
     C:\Demonstrator: FAIL — "Without resolution", empty <Canvases/>.  → nothing to display, no entry point
6. Does each <CAT>.cfg register ALL its symbols/faceplates?
     C:\Demonstrator: FAIL — only sDefault hardcoded.
7. Does an HMI_NET logical device exist with a StartCanvas property?
     C:\Demonstrator: FAIL — absent.  → no deployment target
8. Is it bound in topology (logicalDeviceId → equipment RuntimeDEO with IP + 61999/51443)?
     C:\Demonstrator: FAIL — no Workstation/HMIP6 equipment.
9. Does HMI.dll build under the EAE/SharpDevelop toolchain (NOT dotnet build)?
10. Is the target reachable and is a cert enrolled for that RuntimeDEO.uuid?
11. Deploy → does the runtime start on FirstCanvas?
12. Do TagName values resolve to live FB instances on M262/M580/BX1?
13. Do writes (MCNF/MREQO/cmd_event) reach the correct controller?
```
`C:\Demonstrator` fails first at **step 3**, and independently at 4, 5, 6, 7, 8.

---

## 14. Validation Plan

1. **Structural archive comparison** — regenerate and diff the HMI + IEC61499 file lists against the reference; assert screen count, csproj `.cnv.cs` ref count, `<Canvas>` count in `CanvasesResolutionList`.
2. **EAE open/parse** — solution opens with **no** conversion/repair prompt, no "Repair Instances", no Solution Integrity error (this is where dangling `TagName`/`.hcf` IDs surface).
3. **HMI compile** — build under the EAE toolchain; `HMI.dll` produced; zero `WarningsAsErrors 0618` hits.
4. **Binding completeness** — every placed symbol's `TagName` exists as an `<FB ID=…>` in the syslay **and** as a `Mapping=` in some `.sysres`; every `<Symbol>` in every `.cfg` has its files on disk (the reference itself **fails** this for Station/Sensor/Robot/Seven_State — §15 WP2).
5. **Broken-reference scan** — no `.cfg` symbol without files; no csproj item without a file; no `DependentUpon` pointing at a missing sibling.
6. **OPC UA namespace validation** — *only if OPC UA is ever enabled*; today assert the opposite: 0 `Enabled="true"` (so nothing is unintentionally published).
7. **Offline/simulated runtime** — deploy to the `(Local Test)` profile; confirm the start canvas loads and symbols resolve.
8. **Read-only live data** — with controllers running, confirm state/sensor/step values update on MainScreen and ActuatorsScreen.
9. **Controlled command validation** — Setup mode only, one actuator, on the bench with the rig **safe**: `toWork`/`toHome` via `cmd_event` reaches `ActuatorCore.setup_event`; verify no command is accepted outside Setup.
10. **Alarm validation** — force a move timeout; confirm 1001/1002 renders with cause and recovery text.
11. **Communication-loss** — power down one controller; confirm the HMI shows stale/bad quality for that controller's tags only (**[U]** behaviour unverified — see §15 WP6).
12. **Reboot persistence** — HMI restarts on the correct start canvas; controllers resume from boot project.

---

## 15. Proposed Implementation Backlog (not implemented)

| WP | Objective | Inputs | Outputs | Deps | Acceptance | Risk |
|---|---|---|---|---|---|---|
| **WP0** | Freeze ground truth; capture the reference as a golden fixture | main archive | `Ground Truth/DemonstratorWithHMI` + a file-inventory baseline | — | Inventory reproducible | Low |
| **WP1** | **Register what already exists**: emit `HMI.csproj` item groups for the 9 deployed faceplate folders | existing tree | csproj with N `.cnv.cs`/`.def.cs`/resx items | WP0 | `HMI.dll` compiles with faceplates in it | Low |
| **WP2** | **Complete `<CAT>.cfg`**: enumerate all symbols/faceplates per CAT instead of hardcoding `sDefault`; make it overwrite-not-skip | CAT zip contents | correct `.cfg` per CAT | WP1 | Every `<Symbol>` has files; `_setup`/`sFault` registered | Low |
| **WP3** | **Identity ledger** — persist `ComponentID → FB ID`; read-then-generate | Control.xml | `hmi-identity.json` | — | Rename a component: IDs unchanged | **Med** |
| **WP4** | **HMI presentation model** — the §10 schema + a `HmiModelEmitter` (standalone, outside the control pipeline → zero gate impact) | Control.xml, ComponentRegistry, InterlockPlan, recipes | `hmi-model.json` | WP3 | Deterministic; regenerates identically | Med |
| **WP5** | **Screen generator** — `.cnv.cs` + `.cnv.Designer.cs` + csproj items + `CanvasesResolutionList.xml` from the model | WP4 | 1..N screens with faceplate instances bound by `TagName` | WP1-4 | EAE opens; symbols resolve; navigation works | **High** (serializer invariants) |
| **WP6** | **HMI_NET device + topology binding** — emit the `.sysdev`, `StartCanvas` property, Workstation/HMIP6 equipment, `logicalDeviceId`, 61999/51443 | device.yml + WP4 | deployable target | WP5 | Deploy&Diagnostic lists the HMI device | **High** |
| **WP7** | **State names + alarms + interlock text** from Control.xml (removes hardcoded switches; answers the meeting's #1 ask) | Control.xml, InterlockPlan | `states{}`, `alarms[]`, `AlarmClasses.xml` | WP4 | Faceplates show names not ints | Med |
| **WP8** | **HMI gate** — extend `_gate` with HMI assertions (screen count, csproj refs, canvas topology, TagName resolvability) | — | automated regression | WP1-5 | Gate fails on a dropped screen | Low |
| **WP9** | *(optional, separate decision)* Enable OPC UA — flip `Enabled`, add endpoints/certs | — | published address space | WP6 | Server browsable | **High** — new architecture, new attack surface |

**Sequencing note:** WP1+WP2 alone convert `C:\Demonstrator` from "empty shell" to "compiling faceplate library" with very little risk, and are worth doing before any screen generation.

---

## 16. Final Go/No-Go Checklist

Before deploying a Mapper-generated HMI to the Harmony target, **all** must be true:

1. ☐ Ground truth is the **main** `DemonstratorWithHMI.sln.zip` (not the timestamped archive).
2. ☐ `IEC61499.dfbproj` has `<HMIProject>HMI</HMIProject>` **and** `<CATInstancesHaveIds>true</CATInstancesHaveIds>`.
3. ☐ `HMI.csproj` registers **every** canvas: `<Compile><Canvas>true</Canvas>` + `Designer` (`DependentUpon`, filename-only) + `<EmbeddedResource>` resx.
4. ☐ At least one work-area screen exists and `CanvasesResolutionList.xml` declares a real resolution, `StartCanvasClass`, `<Topology FirstCanvas=…>` and a non-empty `<Canvases>`.
5. ☐ Every `<CAT>.cfg` lists all symbols/faceplates and every referenced file exists on disk.
6. ☐ Every placed `TagName` resolves to a syslay `<FB ID>` **and** a `.sysres` `Mapping=`; identity ledger applied so IDs did not churn.
7. ☐ An `HMI_NET` logical device exists with the `StartCanvas` property, bound in topology to a **single, non-colliding** IP with ports 61999/51443.
8. ☐ The deployment target is confirmed by workshop evidence (MAC/hostname of the box on 192.168.1.2) — **not** assumed to be HMIP6 vs Workstation.
9. ☐ A runtime certificate is enrolled for that `RuntimeDEO.uuid`; no certificate copied from another project.
10. ☐ `HMI.dll` builds under the **EAE/SharpDevelop** toolchain (not `dotnet build`) with no errors.
11. ☐ EAE opens the solution with no repair/integrity prompt; `_gate` HMI assertions pass.
12. ☐ Controllers are deployed and running; boot project set; the three CrossComm channels are up.
13. ☐ Read-only validation passes before any write is attempted.
14. ☐ Write validation is limited to Setup mode, one actuator, with the rig confirmed **mechanically safe** (the bench is currently flagged unsafe — damaged clamp / swivel collision risk).
15. ☐ It is understood and recorded that **OPC UA is off by design**; nothing in this HMI path depends on it, and enabling it is a separate, security-reviewed decision.
16. ☐ No claim is made that this HMI is a safety-rated layer; command/status separation is verified (requested vs confirmed state shown distinctly).

---

## Appendix A — Root-Cause Hypotheses, Ranked

| # | Hypothesis | Verdict | Confidence | Discriminating test |
|---|---|---|---|---|
| 3 | Screens exist but registration metadata missing | **CONFIRMED (worst form: screens don't exist AND registration is absent)** — csproj 0 canvas refs; `CanvasesResolutionList` stub | **Certain** | `grep -c '\.cnv\.cs' HMI.csproj` → 0 |
| 20 | Navigation / startup / resolution config incomplete | **CONFIRMED** — `StartCanvasClass=""`, `<Canvases/>` empty | **Certain** | read `CanvasesResolutionList.xml` |
| 12 | Harmony deployment target absent from topology | **CONFIRMED** — no `HMI_NET` device, no Workstation/HMIP6 equipment in D | **Certain** | `find -name '*.sysdev' | xargs grep HMI_NET` |
| 16 | Mapper lacks screen-layout / presentation semantics | **CONFIRMED** | **Certain** | no layout fields in any input |
| 4 | CAT faceplates exist but instance bindings absent | **CONFIRMED** — faceplates on disk, no instances placed anywhere | **Certain** | 0 `TagName` in D |
| 22 | Wrong archive treated as ground truth | **CONFIRMED risk** — ts archive is materially incomplete | **Certain** | file-list diff (§3) |
| 17 | Reference HMI contains substantial manual design | **CONFIRMED** — screens + faceplate `.cnv.cs` logic + `ImageBytes` are hand/EAE-authored | High | timestamps + `// TODO: Implement` bodies |
| 18 | Designer files cannot be safely generated by text patterns alone | **PARTIALLY CONFIRMED** — Designer/def/event/cnv.xml *are* deterministic; resx `ImageBytes` and theme `.cs` are not | High | §12 file table |
| 8 | Stable UIDs change per run | **CONFIRMED as latent risk** — `SHA256(ComponentID)`, no ledger; sysres nibble-flip already caused dup instances | High | rename a component, re-run, diff IDs |
| 5 | `_HMI.fbt`/adapters missing or inconsistent | **REFUTED for presence** (8 fbt + 2 adp in D) — but `.cfg` registers only `sDefault` | High | file counts |
| 2 | HMI.csproj not registered in `.sln` | **REFUTED** — correctly registered | Certain | read `Demonstrator.sln` |
| 1 | No HMI project at all | **REFUTED** | Certain | `HMI.csproj` exists |
| 6/7 | OPC UA exposure / system config incomplete | **TRUE BUT IRRELEVANT** — 71/71 disabled *by design*; HMI never uses OPC UA | High | grep `opc.tcp` → 0 |
| 15 | Certificates/trust prevent runtime comms | **REFUTED as a current blocker** — EcoRT certs auto-enrol; no OPC UA trust needed | Med | cert table §7 |
| 21 | HMI connects to one controller but not all | **REFUTED** — one flat TagName namespace spans M262+M580+BX1 | High | TagName→resource table §8 |
| 11 | HMIP6 `.1/.2` confused with RevPi | **REAL RISK, currently latent** — `.1` is HMIP6's *unprovisioned* container; RevPi is `.6`; three objects share `.2` | High | topology JSON §8 |
| 10 | HMI points to wrong controller addresses | **REFUTED** — HMI names no address at all | High | no IP in any `.cnv` |
| 13 | HMI runtime not installed/running on target | **[U]** — cert exists for `HMI_Linux_1235`@.2, but no deployment record in `snapshot.xml` | Low–Med | EAE Runtime Manager connect |
| 19 | EAE library/assembly versions differ | **[U]** — reference pins 24.1.0.17–.33; must be read from the installed set | Low–Med | compare installed library versions |
| 9 | HMI controls reference stale instances | Not yet — no controls exist in D | — | after WP5 |
| 14 | Builds locally but no deployment configuration | **CONFIRMED (consequence of 12)** | Certain | no HMI in `snapshot.xml` |

---

## Appendix B — HMI Quality & Safety Observations (read-only)

**Good, already present [F]:** command vs status is properly separated at the port level (mode *request* `Mode`/`MCNF` vs *actual* `System_Mode`/`MSTS`; `toWork` command vs `toWorkPLC` coil echo vs `atWork` sensor); manual step-execute is **momentary** (MouseDown true / MouseUp false) with a ready→execute→complete handshake; actuator jog is **mode-gated in the controller** (`cmd_event → ActuatorCore.setup_event`), not merely greyed out in the UI; read ports are `AccessLevel=1 CurrentRead Locked="true"` so they can never become writable; no interlock bypass/override write exists anywhere.

**Weak or absent [F]:** no rig-specific alarm classes (fault text hardcoded in `.cnv.cs`); `Fault_Reset`/`LL_Fault_Status` wired end-to-end but never driven or displayed; `MoveAllowed`/`ActiveRuleIndex`/`ActiveSourceID` reach the faceplate and are never shown; **Station's full mode/cycle/fault-reset contract is deployed on M262 and M580 with no faceplate to reach it**; no confirmation prompts; no stale-data/communication-loss/bad-quality indication found; no role model beyond `SecurityToken=0xFFFFFFFF` (all groups permitted on every symbol); no E-stop representation; `InsecureApplication.Enable=True` on all three controllers.

**This HMI is not a safety-rated control layer and nothing in this report should be read as a safety claim.** Operational interlocks shown here are soft logic; real machine safety is a separate hardware/safety-PLC concern.

---

## Appendix C — Key File References

**Reference (analysis copy `…\hmi_analysis\main`)**
`DemonstratorWithHMI.sln:7` · `HMI\HMI.csproj:3-24,69-157,176-197,232,239-259,275-289,318-320` · `HMI\MainScreen.cnv.cs` / `.cnv.Designer.cs:33,44,74-81,105,113-124` · `HMI\StartCanvas_2.cnv.Designer.cs:80,179,197` · `HMI\CanvasesResolutionList.xml:2-4,27-31` · `HMI\Alarms\{AlarmClasses,SystemAlarmClasses}.xml` · `HMI\Five_State_Actuator_CAT\{*.def.cs:14-38,66-108; *.event.cs:32-70,89-107,169-224,484-653; *_sDefault.cnv.xml; *_sDefault.cnv.Designer.cs:53,81,94-102,339,354; *_sFault.cnv.cs:41-53; *_sSetup.cnv.cs:26-52,Designer:278-305,329,345,371}` · `HMI\Area_CAT\Area_CAT_sDefault.cnv.cs:31,36-126,183-211` · `HMI\Process1_Generic\*_sManual.cnv.cs:27-36` · `IEC61499\IEC61499.dfbproj:11,14-15,168-171,219-229,494-498,702` · `IEC61499\<CAT>\<CAT>.cfg:2-4,13` · `IEC61499\<CAT>\<CAT>_HMI.fbt` · `IEC61499\{AreaHMIAdptr,StationHMIAdptr}.adp` (+ `StationHMIAdptr.adp:71-72` dangling `PNREQ`) · `IEC61499\Area_CAT\Area_CAT.fbt:10,14,18-27,33-35,44-55` · `IEC61499\Area.fbt:59,65,71,90,96,102` · `IEC61499\Five_State_Actuator_CAT.fbt:106,448,453,456,461,665-712` · `IEC61499\<CAT>\<CAT>_HMI.opcua.xml` (71 vars, all `Enabled="false"`) · `IEC61499\System\…\a441cfb6-…sysdev` + `F513CAE3-….Properties.xml` · `IEC61499\System\…\8e5bdb40-…` (orphan) · `IEC61499\System\snapshot.xml` · `IEC61499\SnapshotCompiles\…\CrossComm\*\*.commdesc.xml` · `Topology\Equipment_{HMIB1X_1,HMIP6_1,Workstation_1,M262dPAC_1,M580dPAC_1,Switch_1,EtherNetIPDevice_1}.json` · `General\Certificates\Certificates.xml`

**Mapper (`C:\VueOneMapper`)**
`MapperUI\MapperUI\Forms\MainForm.cs:468-473,925,971,975-1104` · `_gate\Program.cs:31-35,59,110-118,178-179,250-397` · `CodeGen\CodeGen\Planning\SystemLayoutInjector.cs:951-952,968-980,2328-2332` · `Artefacts\Resource\ResourceWireEmitter.cs:68-74` · `Artefacts\Resource\SysresFbMirror.cs:140-142,179-181,536-548` · `Artefacts\Templates\TemplateArtifactDeployer.cs:156-236,303-312` · `Artefacts\Templates\TemplateLibraryDeployer.cs:48-144,464-466` · `Artefacts\Templates\HmiTemplatePatcher.cs:14-58,67-103` · `Artefacts\Templates\ProcessRuntimeTemplatePatcher.cs:407-445` · `Devices\Common\OpcuaCompanionEmitter.cs:7-9,26,34,57,88-95` · `Devices\Common\DfbprojRegistrar.cs:13-52,262-286,369-383` · `Planning\Components\FBIdGenerator.cs:9-18` · `Mapping\{ComponentRegistry.cs:91-149, ControllerMap.cs:12-19, TemplateMap.cs:15-94, LayoutGrid.cs}` · `Input\Settings\MapperConfig.cs:164-234,283-320` · `Config\{smc-rig.yml,recipes.yml,interlock.yaml,device.yml,telemetry.yml,config.yaml}` · `Template Library\CAT\*.cat.zip` (each contains `HMI/<CAT>/…` + `IEC61499/<CAT>/…`) · `MapperTests\MapperTests.csproj:22-64` (all 23 tests `<Compile Remove>`d)

**Generated (`C:\Demonstrator\Demonstrator`)**
`Demonstrator.sln` (HMI `{5C9CB6C4-057C-4101-A72E-3087325B20F6}`) · `HMI\HMI.csproj` (8,215 B, 3 Compile items, 0 canvas refs, 2 malformed `<none Include=…>`) · `HMI\CanvasesResolutionList.xml` (769 B stub) · `HMI\<9 CAT folders>` (orphaned) · `IEC61499\System\…\0000000{1..4}\opcua.xml` (7 × 251 B stubs)

**Other**
`…\Jyotsna\Deployment Tutorial Five State Actuator CAT\Five_State_Actuator_CAT_{Tutorial_Part3,Deployment_Tutorial_Part4}.docx` (CAT+HMI creation; canvas-by-resolution; drag-drop instance placement; login → Deploy To Run → Set Active Project As Boot Project) · `…\Jyotsna\Latest\Process1_Generic.*.cat\{HMI,IEC61499}` (CAT export bundle shape) · `C:\VueOneMapper\Docs\{ARCHITECTURE.md,INVARIANTS.md,SMC_Rig_HMI_Research_and_Design.md,STATESYNC_UNS_BRIDGE_DESIGN.md,WEB_HMI_GENERATION_DESIGN.md}`

---

*Read-only investigation. No Mapper code, EAE project, controller, or HMI was modified. Archives were extracted only to a temporary analysis directory; originals were never resaved.*
