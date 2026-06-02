# After-Update Workflow (MapperUI)

**Claude does this automatically after every code change — never handed off to the user.**

> **IGNORE THE MAPPER TESTS.** Do NOT build, run, or gate on `MapperTests`
> (`dotnet test`, `SimulatorEndToEndHarness`, `TemplateDeployerTests`, etc.). They
> are not the verification path — the real check is a clean EAE Build/Deploy of the
> Test Simulator output. Never enable a `<Compile Remove>`-excluded test file or add
> diagnostic tests. Build only the MapperUI project (which pulls in CodeGen).

After editing any Mapper/CodeGen source, run these in order:

1. **Build** — recompiles CodeGen + MapperUI (build the MapperUI project ONLY, never the test project).
   ```powershell
   dotnet build MapperUI/MapperUI/MapperUI.csproj -c Debug
   ```
2. **Kill MapperUI** — it loads `CodeGen.dll` into its process, so it must be stopped before the build can replace the DLL (and before a fresh launch).
   ```powershell
   Stop-Process -Name MapperUI -Force -ErrorAction SilentlyContinue
   ```
   > If the build fails with `CodeGen.dll ... locked by MapperUI`, kill first, then build.
3. **Relaunch** — start the freshly built exe so the changes are live.
   ```powershell
   Start-Process "C:\VueOneMapper\MapperUI\MapperUI\bin\Debug\net10.0-windows\MapperUI.exe"
   ```

The user then just clicks **Test Simulator** (or Test Runtime) on the already-running, up-to-date MapperUI — no rebuild step on their side.

## Notes
- Order when the DLL is locked: **kill → build → relaunch**.
- **Mapper tests are ignored** — do not run `dotnet test` as a gate. Verify in EAE instead.
- Verify the relaunch: `Get-Process -Name MapperUI` should show a PID.
- Only mess with the **Test Simulator** path (`MainForm_simulator.cs` + sim-only
  `SimulatorPostProcessor` methods). Do NOT change the **Test Runtime** / rig button
  (`MainForm.cs` `btnTestStation1_Click`) or the template `.cat.zip`/`.Basic.zip` files.
