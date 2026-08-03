using System;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    // Commandability by CAT shape (mirrors ResolveActuatorFBType). Five-state: 1->AtWork(2), 3->Home(0);
    // seven-state centre-home: 1->Work1(2), 3->Work2(4), 5->Home(0).
    public static class RecipeCommandVocabulary
    {
        public static bool IsSevenStateCommandable(VueOneComponent t) =>
            t != null && !IsSensorOrProcess(t) && IsSevenShape(t);

        private static bool IsSensorOrProcess(VueOneComponent t) =>
            string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase);

        private static bool IsSevenShape(VueOneComponent t) =>
            t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t);
    }
}
