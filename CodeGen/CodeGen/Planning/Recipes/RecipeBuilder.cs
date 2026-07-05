namespace CodeGen.Translation.Process
{
    // Shared recipe-array writer. NextStep auto-chains to row+1 for CMD/WAIT; the END row's NextStep
    // is caller-supplied (cyclic 0 vs self-park = the END row's own index).
    internal sealed class RecipeBuilder
    {
        private readonly RecipeArrays _a;

        public RecipeBuilder(RecipeArrays arrays) { _a = arrays; }

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
