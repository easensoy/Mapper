namespace CodeGen.Translation.Process
{
    /// <summary>
    /// Shared low-level recipe-array writer (extracted 2026-06-19, Step 2 slice 1). Owns the
    /// common CMD / WAIT / END append operations that each recipe section used to duplicate as
    /// local functions. The writes are byte-identical to those originals (same StepType / Cmd /
    /// Wait / NextStep sequence), so migrating a section onto this builder does NOT change the
    /// generated recipe arrays. NextStep auto-chains to row+1 for CMD/WAIT; the END row's
    /// NextStep is supplied by the caller (cyclic 0 vs self-park = the END row's own index).
    /// </summary>
    internal sealed class RecipeBuilder
    {
        private readonly RecipeArrays _a;

        public RecipeBuilder(RecipeArrays arrays) { _a = arrays; }

        /// <summary>Row index the NEXT appended step will occupy (= current row count).</summary>
        public int Count => _a.StepType.Count;

        public void AddCmd(string target, int cmdState)
        {
            int row = _a.StepType.Count;
            _a.StepType.Add(StepType.Cmd);
            _a.CmdTargetName.Add(target);
            _a.CmdStateArr.Add(cmdState);
            _a.Wait1Id.Add(0);
            _a.Wait1State.Add(0);
            _a.NextStep.Add(row + 1);
        }

        public void AddWait(int waitId, int waitState)
        {
            int row = _a.StepType.Count;
            _a.StepType.Add(StepType.Wait);
            _a.CmdTargetName.Add(string.Empty);
            _a.CmdStateArr.Add(0);
            _a.Wait1Id.Add(waitId);
            _a.Wait1State.Add(waitState);
            _a.NextStep.Add(row + 1);
        }

        public void AddEnd(int nextStep)
        {
            _a.StepType.Add(StepType.End);
            _a.CmdTargetName.Add(string.Empty);
            _a.CmdStateArr.Add(0);
            _a.Wait1Id.Add(0);
            _a.Wait1State.Add(0);
            _a.NextStep.Add(nextStep);
        }
    }
}
