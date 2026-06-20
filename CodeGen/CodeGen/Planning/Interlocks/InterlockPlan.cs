namespace CodeGen.Translation.Interlocks
{
    public sealed record InterlockPlan(int Count, int[] From, int[] To, int[] Src, int[] Blocked)
    {
        public static InterlockPlan Empty(int cap) =>
            new(0, new int[cap], new int[cap], new int[cap], new int[cap]);
    }
}
