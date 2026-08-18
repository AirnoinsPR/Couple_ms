using System.Linq;
using Sharp.Shared.Definition;
using Sharp.Shared.Objects;

namespace Ins.Couple;

public sealed partial class Couple
{
    private void TimerUpdate()
    {
        foreach (var user in _users.Values.ToArray())
        {
            if (user.Status is not (Status.Requester or Status.Proposed or Status.BreakingUp or Status.PendingProposal))
            {
                continue;
            }

            user.Num += 1;
            if (user.Num < 10)
            {
                continue;
            }

            var client = GetClientBySteamId(user.SteamID);

            switch (user.Status)
            {
                case Status.Requester:
                    ExpireRequester(user, client);
                    break;
                case Status.Proposed:
                case Status.PendingProposal:
                    ExpireProposalTarget(user, client);
                    break;
                case Status.BreakingUp:
                    RestoreUserToMarried(user.SteamID);
                    RestoreUserToMarried(user.SpouseSteamID);
                    if (IsValidClient(client))
                    {
                        Chat(client!, $"{ChatColor.Red}请求超时");
                    }
                    break;
            }
        }
    }

    private void ExpireRequester(User requesterUser, IGameClient? requesterClient)
    {
        var pendingUser = _users.Values.FirstOrDefault(user =>
            user.RequesterSteamID == requesterUser.SteamID
            && user.Status is Status.Proposed or Status.PendingProposal);

        if (pendingUser is not null)
        {
            ResetPendingProposal(requesterUser, pendingUser);

            var pendingClient = GetClientBySteamId(pendingUser.SteamID);
            if (IsValidClient(pendingClient))
            {
                Chat(pendingClient!, $"{ChatColor.Red}请求超时");
            }
        }
        else
        {
            ResetUserToNone(requesterUser.SteamID);
        }

        if (IsValidClient(requesterClient))
        {
            Chat(requesterClient!, $"{ChatColor.Red}请求超时");
        }
    }

    private void ExpireProposalTarget(User proposedUser, IGameClient? proposedClient)
    {
        ulong requesterSteamId = proposedUser.RequesterSteamID;
        ResetRequester(requesterSteamId);
        proposedUser.Status = Status.None;
        proposedUser.RequesterSteamID = 0;
        proposedUser.Num = 0;

        var requester = GetClientBySteamId(requesterSteamId);
        if (IsValidClient(requester))
        {
            Chat(requester!, $"{ChatColor.Red}请求超时");
        }

        if (IsValidClient(proposedClient))
        {
            Chat(proposedClient!, $"{ChatColor.Red}请求超时");
        }
    }
}
