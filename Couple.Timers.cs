using System.Linq;
using Sharp.Shared.Definition;

namespace Ins.Couple;

public sealed partial class Couple
{
    private void TimerUpdate()
    {
        if (!_isDbConnected)
        {
            return;
        }

        foreach (var user in _users.Values.ToArray())
        {
            if (user.Status is Status.None)
            {
                continue;
            }

            if (user.Status is not (Status.Proposed or Status.BreakingUp))
            {
                continue;
            }

            if (user.Num == 10)
            {
                var client = GetClientBySteamId(user.SteamID);
                if (!IsValidClient(client) || user.SteamID == 0)
                {
                    continue;
                }

                if (user.Status == Status.Proposed)
                {
                    var requester = GetClientBySteamId(user.RequesterSteamID);
                    if (IsValidClient(requester) && _users.TryGetValue(user.RequesterSteamID, out var requesterUser))
                    {
                        requesterUser.Status = Status.None;
                        Chat(requester!, $"{ChatColor.Red}请求超时");
                    }

                    user.Status = Status.None;
                    user.RequesterSteamID = 0;
                }

                if (user.Status == Status.BreakingUp)
                {
                    user.Status = Status.Married;
                }

                user.Num = 0;
                Chat(client!, $"{ChatColor.Red}请求超时");
            }

            user.Num += 1;
        }
    }
}
