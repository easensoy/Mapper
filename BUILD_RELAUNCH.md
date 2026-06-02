# After-Update Workflow (MapperUI)

**Claude does this automatically after every code change — never handed off to the user.**

After editing any Mapper/CodeGen source, run these in order:

1. **Build** — recompiles CodeGen + MapperUI.
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
- The harness (`MapperTests`) also references `CodeGen.dll`; if a running MapperUI blocks `dotnet test`, kill MapperUI first.
- Verify the relaunch: `Get-Process -Name MapperUI` should show a PID.
