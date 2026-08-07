# Build the Mapper and put it where VueOne will actually run it.
#
# CodeGen.dll and VueOneMapperHiddenRunner are ONE deployable unit. The runner links CodeGen's public
# API directly, so shipping a fresh CodeGen.dll beside a stale runner produces a MissingMethodException
# the moment VueOne presses Generate  -  which is exactly what happened on 2026-08-07, because the runner
# was rebuilt from source in its own tree but never copied into the directory VueOne launches. This
# script is the only supported way to propagate either of them, so they cannot diverge again.
#
# Compiling the runner is NOT sufficient evidence: a source-only compile passed while the deployed
# binary was months old. Step 4 therefore EXECUTES the shipped binary end to end and requires the
# generation to report completion; nothing propagates outward until it does.
#
#   pwsh Tools\propagate.ps1
#   pwsh Tools\propagate.ps1 -Model "...\SMC_Vue2VC_With_Processes_vc\Control.xml"

[CmdletBinding()]
param(
    # Twin used for the integration check. Any model will do; it is generated into a scratch tree.
    [string] $Model = "$env:USERPROFILE\OneDrive\Documents\VueOne\system\SMC_Vue2VC_With_Processes_se\Control.xml",
    # A generated EAE project the runner can regenerate over; it refuses to start without one.
    [string] $BaseTree = 'C:\_gate\base',
    [string] $ScratchRoot = 'C:\_gate\propagate_check',
    # Run only the integration check against whatever is already deployed. Answers "is the runner
    # sitting beside this CodeGen.dll actually able to generate?" without building or propagating.
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'

$Repo      = Split-Path -Parent $PSScriptRoot
$CodeGen   = Join-Path $Repo 'CodeGen\CodeGen\CodeGen.csproj'
$MapperUI  = Join-Path $Repo 'MapperUI\MapperUI\MapperUI.csproj'
$Shipped   = Join-Path $Repo 'MapperUI\MapperUI\bin\Debug\net10.0-windows'
$RunnerDir = 'C:\V-Dev\VueOneVcVersion\VueOneVcVersion\vueone_vc\Development\VueOneMapperHiddenRunner'
$RunnerOut = Join-Path $RunnerDir 'bin\Debug\net10.0'

# Every location that must carry the same CodeGen.dll + Config + runner.
$Targets = @(
    $RunnerOut,
    'C:\V-Dev\VueOneVcVersion\VueOneVcVersion\vueone_vc\Published_Alper',
    'C:\V-Dev\VueOneFullVersion\VueOneFullVersion\Published_Alper'
)

$RunnerFiles = @(
    'VueOneMapperHiddenRunner.exe',
    'VueOneMapperHiddenRunner.dll',
    'VueOneMapperHiddenRunner.deps.json',
    'VueOneMapperHiddenRunner.runtimeconfig.json',
    'VueOneMapperHiddenRunner.pdb'
)

function Step($n, $text) { Write-Host ""; Write-Host "[$n] $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "FAILED: $text" -ForegroundColor Red; exit 1 }

function Build($proj) {
    $out = & dotnet build $proj -c Debug --no-incremental -v q --nologo
    $summary = $out | Select-String -Pattern '\d+ (Error|Warning)\(s\)'
    foreach ($line in $summary) { Write-Host "    $($line.ToString().Trim())" }
    if ($LASTEXITCODE -ne 0) { Fail "build failed: $proj" }
    if ($out -match '\b[1-9]\d* Warning\(s\)') { Fail "build emitted warnings: $proj" }
}

# Stage every file, then swap them all in, so no half-updated set is ever observable.
function CopySet($from, $to, $names) {
    foreach ($n in $names) {
        if (-not (Test-Path (Join-Path $from $n))) { Fail "missing source file: $n in $from" }
        Copy-Item (Join-Path $from $n) (Join-Path $to "$n.new") -Force
    }
    foreach ($n in $names) {
        if (-not (Test-Path (Join-Path $to "$n.new"))) { Fail "staging incomplete: $n in $to" }
    }
    foreach ($n in $names) { Move-Item (Join-Path $to "$n.new") (Join-Path $to $n) -Force }
}

if (-not $CheckOnly) {
    Step 1 'Build CodeGen and MapperUI'
    Build $CodeGen
    Build $MapperUI

    Step 2 "Build the hidden runner against the CodeGen.dll just produced"
    if (-not (Test-Path $RunnerDir)) { Fail "runner source not found: $RunnerDir" }
    Build (Join-Path $RunnerDir 'VueOneMapperHiddenRunner.csproj')

    Step 3 'Install the runner beside CodeGen.dll, where VueOne launches it'
    CopySet $RunnerOut $Shipped $RunnerFiles
    Write-Host "    $Shipped"
}

Step 4 'Run the SHIPPED runner binary end to end (a compile alone would not have caught the 08-07 break)'
if (-not (Test-Path $Model)) { Fail "twin not found: $Model" }
$seed = $BaseTree
if (-not (Test-Path $seed)) { $seed = 'C:\Demonstrator' }
if (-not (Test-Path $seed)) { Fail "no seed project tree; expected $BaseTree or C:\Demonstrator" }
if (Test-Path $ScratchRoot) { Remove-Item $ScratchRoot -Recurse -Force }
New-Item -ItemType Directory $ScratchRoot | Out-Null
Copy-Item (Join-Path $seed '*') $ScratchRoot -Recurse -Force

$before = @(Get-ChildItem (Join-Path $Shipped 'Output') -Filter 'VueOneMapperHiddenRunner_*.log' `
            -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
& (Join-Path $Shipped 'VueOneMapperHiddenRunner.exe') --mapper-dir $Shipped --control $Model --output-root $ScratchRoot | Out-Null
$rc = $LASTEXITCODE

# The log must be one THIS run created. A runner that dies before opening its log leaves the previous
# run's file newest, and reading that would show a stale [Done] and pass a broken deployment.
$log = Get-ChildItem (Join-Path $Shipped 'Output') -Filter 'VueOneMapperHiddenRunner_*.log' |
       Where-Object { $before -notcontains $_.FullName } |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $log) { Fail "the runner produced no log of its own (exit $rc)  -  it failed before starting" }
$text = Get-Content $log.FullName -Raw

# Exit code alone is not evidence: require the line the generator writes only after it finishes.
if ($text -notmatch '\[Done\] IEC61499 generation completed') {
    Write-Host ($text -split "`n" | Select-String -Pattern '\[Error\]' | Select-Object -First 5)
    Fail "the shipped runner did not complete (exit $rc). Log: $($log.FullName)"
}
if ($rc -ne 0) { Fail "the shipped runner exited $rc. Log: $($log.FullName)" }
foreach ($v in @('\[Connections\] PASS', '\[Hcf\]\[Validate\] PASS', '\[Parity\] PASS', '\[BX1\]\[Scanner\] OK')) {
    if ($text -notmatch $v) { Fail "validator did not pass: $v. Log: $($log.FullName)" }
}
Write-Host '    [Done] reported; Connections / Hcf / Parity / BX1 Scanner all pass'

if ($CheckOnly) {
    Write-Host ''
    Write-Host 'CHECK PASSED. The deployed runner and CodeGen.dll are a working pair.' -ForegroundColor Green
    exit 0
}

Step 5 'Propagate CodeGen.dll, Config and the runner to every location'
foreach ($t in $Targets) {
    if (-not (Test-Path $t)) { Fail "target not found: $t" }
    CopySet $Shipped $t @('CodeGen.dll', 'CodeGen.pdb')
    $cfg = Join-Path $t 'Config'
    if (Test-Path $cfg) { Remove-Item $cfg -Recurse -Force }
    Copy-Item (Join-Path $Shipped 'Config') $cfg -Recurse -Force
    # Published_Alper hosts VueOne itself, which launches the runner from the Mapper directory, so it
    # needs CodeGen but not the runner binaries; the runner's own output directory does.
    if ($t -eq $RunnerOut) { CopySet $Shipped $t $RunnerFiles }
    Write-Host "    $t"
}

Step 6 'Prove every location carries the identical files'
$ref = (Get-FileHash (Join-Path $Shipped 'CodeGen.dll') -Algorithm SHA256).Hash
$bad = 0
foreach ($t in (@($Shipped) + $Targets)) {
    $h = (Get-FileHash (Join-Path $t 'CodeGen.dll') -Algorithm SHA256).Hash
    if ($h -eq $ref) { $s = 'MATCH ' } else { $s = 'DIFFER'; $bad++ }
    Write-Host "    $s CodeGen.dll  $t"
}
foreach ($f in (Get-ChildItem (Join-Path $Shipped 'Config') -File)) {
    $r = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    foreach ($t in $Targets) {
        $h = (Get-FileHash (Join-Path $t "Config\$($f.Name)") -Algorithm SHA256).Hash
        if ($h -ne $r) { Write-Host "    DIFFER Config\$($f.Name) in $t"; $bad++ }
    }
}
foreach ($n in $RunnerFiles) {
    $r = (Get-FileHash (Join-Path $Shipped $n) -Algorithm SHA256).Hash
    $h = (Get-FileHash (Join-Path $RunnerOut $n) -Algorithm SHA256).Hash
    if ($h -ne $r) { Write-Host "    DIFFER $n in $RunnerOut"; $bad++ }
}
if ($bad -ne 0) { Fail "$bad file(s) differ across locations" }
Write-Host '    CodeGen.dll, every Config file and every runner file are identical everywhere'

Write-Host ''
Write-Host 'PROPAGATED. VueOne will now launch a runner built against this CodeGen.dll.' -ForegroundColor Green
exit 0
