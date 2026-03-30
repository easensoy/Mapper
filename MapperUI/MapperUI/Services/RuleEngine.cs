// Re-exports from CodeGen so existing MapperUI code continues to compile
// without changing every using statement.
// The canonical types now live in CodeGen.Models and CodeGen.Translation.

global using MappingType = CodeGen.Models.MappingType;
global using MappingRuleEntry = CodeGen.Models.MappingRuleEntry;
global using MappingRuleEngine = CodeGen.Translation.MappingRuleEngine;
