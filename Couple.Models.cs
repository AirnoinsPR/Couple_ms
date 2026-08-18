namespace Ins.Couple;

public sealed partial class Couple
{
    private sealed class User
    {
        public ulong SteamID { get; set; }
        public ulong RequesterSteamID { get; set; }
        public ulong SpouseSteamID { get; set; }
        public Status Status { get; set; } = Status.None;
        public int Num { get; set; }
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

}
