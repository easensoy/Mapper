// A declared template row IS a manifest type, a declared protocol IS a CAT's command vocabulary, a
// declared tap IS its telemetry contract and a declared sequence IS its execution: ONE shape per
// concept, so a template's contract cannot be stated twice and drift. These are the names the
// generator reads them under - an artefact KIND and a type's ROLE rather than a configuration row.
global using TemplateType = CodeGen.Configuration.TemplateDeclaration;
global using ArtefactKind = CodeGen.Configuration.TemplateArtefactKind;
global using TypeRole = CodeGen.Configuration.TemplateRole;
global using CatProtocol = CodeGen.Configuration.CatProtocolDeclaration;
global using CatTelemetryTap = CodeGen.Configuration.TelemetryTapDeclaration;
global using CatPhaseHandoff = CodeGen.Configuration.PhaseHandoffDeclaration;
global using CatExecution = CodeGen.Configuration.CatExecutionDeclaration;
global using ExecutionStep = CodeGen.Configuration.ExecutionStepDeclaration;
