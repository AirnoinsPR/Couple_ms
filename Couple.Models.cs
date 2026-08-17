namespace Ins.Couple;

public sealed partial class Couple
{
    private sealed class User
    {
        public ulong SteamID { get; set; }
        public ulong RequesterSteamID { get; set; }
        public ulong SpouseSteamID { get; set; }
        public string SpouseTitle { get; set; } = string.Empty;
        public Status Status { get; set; } = Status.None;
        public int Num { get; set; }
        public CPSide CPSide { get; set; } = CPSide.Female;
    }

    private enum Status
    {
        None = 0,
        Requester = 1,
        Proposed = 2,
        Married = 3,
        ReqSuccess = 4,
        BreakingUp = 5,
        PendingProposal = 6
    }

    private enum CPSide
    {
        Female = 0,
        Male = 1
    }
}
