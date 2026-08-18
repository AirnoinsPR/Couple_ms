using System.Threading.Tasks;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;

namespace Ins.Couple;

public sealed partial class Couple
{
    public void OnClientPutInServer(IGameClient client)
    {
        if (!IsValidClient(client) || !_isDbConnected)
        {
            return;
        }

        LoadClientUser(client, notifyPresence: true);
    }

    private void LoadClientUser(IGameClient client, bool notifyPresence)
    {
        ulong steamId = client.SteamId.AsPrimitive();

        Task.Run(async () =>
        {
            ulong? spouseSteamId = await GetSpouseSteamIdAsync(steamId);

            _modSharp.InvokeFrameAction(() =>
            {
                if (!_isLoaded || !_isDbConnected || !IsValidClient(client))
                {
                    return;
                }

                _users[steamId] = BuildUser(steamId, spouseSteamId);
            });
        });

        if (notifyPresence)
        {
            PushTimer(() => NotifySpousePresence(steamId), 7.5);
        }
    }

    private void ReloadOnlineUsers()
    {
        foreach (var client in _clients.GetGameClients(true))
        {
            if (IsValidClient(client))
            {
                LoadClientUser(client, notifyPresence: false);
            }
        }
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        ulong steamId = client.SteamId.AsPrimitive();

        if (!_users.TryGetValue(steamId, out var user))
        {
            return;
        }

        ClearPendingStateOnDisconnect(steamId, user);

        if (!_isDbConnected || user.Status != Status.Married)
        {
            _users.Remove(steamId);
            return;
        }

        Task.Run(async () => await UpdateLastSeenAsync(steamId));
        _users.Remove(steamId);
    }

    private void ClearPendingStateOnDisconnect(ulong steamId, User user)
    {
        if (user.Status is Status.Proposed or Status.PendingProposal)
        {
            ResetRequester(user.RequesterSteamID);
            return;
        }

        if (user.Status == Status.BreakingUp)
        {
            RestoreUserToMarried(user.SpouseSteamID);
            return;
        }

        if (user.Status == Status.Requester)
        {
            foreach (var pendingUser in _users.Values)
            {
                if (pendingUser.RequesterSteamID == steamId
                    && pendingUser.Status is Status.Proposed or Status.PendingProposal)
                {
                    ResetPendingProposal(user, pendingUser);
                    break;
                }
            }
        }
    }

    public void OnGameDeactivate()
    {
        _users.Clear();
    }

    private User BuildUser(ulong steamId, ulong? spouseSteamId)
    {
        if (!IsSpouseDataValid(spouseSteamId))
        {
            return new User
            {
                SteamID = steamId
            };
        }

        return new User
        {
            SteamID = steamId,
            SpouseSteamID = spouseSteamId!.Value,
            Status = Status.Married
        };
    }

    private void AcceptProposal(IGameClient client)
    {
        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user) || user.Status != Status.Proposed)
        {
            return;
        }

        var requester = GetClientBySteamId(user.RequesterSteamID);
        if (!IsValidClient(requester))
        {
            Chat(client, "对方已离线或不可用");
            return;
        }

        if (!_users.TryGetValue(user.RequesterSteamID, out var requesterUser))
        {
            Chat(client, "对方已离线或不可用");
            return;
        }

        ulong proposedSteamId = client.SteamId.AsPrimitive();
        ulong requesterSteamId = requester!.SteamId.AsPrimitive();
        user.Status = Status.ReqSuccess;
        requesterUser.Status = Status.ReqSuccess;

        Task.Run(async () =>
        {
            bool added = await AddCoupleAsync(proposedSteamId, requesterSteamId);
            if (added)
            {
                await UpdateLastSeenAsync(proposedSteamId);
                await UpdateLastSeenAsync(requesterSteamId);
            }

            _modSharp.InvokeFrameAction(() =>
            {
                var proposedClient = GetClientBySteamId(proposedSteamId);
                var requesterClient = GetClientBySteamId(requesterSteamId);

                if (!added)
                {
                    ResetUsersToNone(proposedSteamId, requesterSteamId);

                    if (IsValidClient(proposedClient))
                    {
                        Chat(proposedClient!, $"{ChatColor.Red}数据库写入失败,求婚未完成");
                    }

                    if (IsValidClient(requesterClient))
                    {
                        Chat(requesterClient!, $"{ChatColor.Red}数据库写入失败,求婚未完成");
                    }

                    return;
                }

                if (!IsValidClient(proposedClient) || !IsValidClient(requesterClient))
                {
                    if (IsValidClient(proposedClient))
                    {
                        Chat(proposedClient!, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在10秒后自动断开,请重新连接至服务器");
                        KickPlayer(proposedClient!, proposedSteamId, 10.0);
                    }

                    if (IsValidClient(requesterClient))
                    {
                        Chat(requesterClient!, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在10秒后自动断开,请重新连接至服务器");
                        KickPlayer(requesterClient!, requesterSteamId, 10.0);
                    }

                    return;
                }

                ChatAll($"{ChatColor.Blue}{requesterClient!.Name} {ChatColor.White}向 {ChatColor.Pink}{proposedClient!.Name} {ChatColor.White}求婚成功,恭喜他们结成连理,幸福美满");
                Chat(proposedClient, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在10秒后自动断开,请重新连接至服务器");
                Chat(requesterClient, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在10秒后自动断开,请重新连接至服务器");
                KickPlayer(proposedClient, proposedSteamId, 10.0);
                KickPlayer(requesterClient, requesterSteamId, 10.0);
            });
        });
        return;
    }

    private void RejectProposal(IGameClient client)
    {
        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user) || user.Status != Status.Proposed)
        {
            return;
        }

        var requester = GetClientBySteamId(user.RequesterSteamID);
        if (!_users.TryGetValue(user.RequesterSteamID, out var requesterUser))
        {
            user.Status = Status.None;
            user.Num = 0;
            user.RequesterSteamID = 0;
            return;
        }

        if (IsValidClient(requester))
        {
            ChatAll($"{ChatColor.Blue}{requester!.Name} {ChatColor.White}向 {ChatColor.Pink}{client.Name} {ChatColor.White}求婚被拒绝,大家快笑他");
        }

        user.Status = Status.None;
        user.Num = 0;
        user.RequesterSteamID = 0;
        requesterUser.Status = Status.None;
        requesterUser.Num = 0;
        requesterUser.RequesterSteamID = 0;
        return;
    }

    private void ConfirmBreakup(IGameClient client)
    {
        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user) || user.Status != Status.BreakingUp)
        {
            return;
        }

        ulong clientSteamId = client.SteamId.AsPrimitive();
        ulong targetSteamId = user.SpouseSteamID;
        bool targetLoaded = _users.TryGetValue(targetSteamId, out var targetUser);

        user.Status = Status.ReqSuccess;
        if (targetLoaded)
        {
            targetUser!.Status = Status.ReqSuccess;
        }

        Task.Run(async () =>
        {
            bool brokenUp = await BreakUpCoupleAsync(clientSteamId, targetSteamId);

            _modSharp.InvokeFrameAction(() =>
            {
                var currentClient = GetClientBySteamId(clientSteamId);
                var currentTarget = GetClientBySteamId(targetSteamId);

                if (!brokenUp)
                {
                    RestoreUserToMarried(clientSteamId);
                    if (targetLoaded)
                    {
                        RestoreUserToMarried(targetSteamId);
                    }

                    if (IsValidClient(currentClient))
                    {
                        Chat(currentClient!, $"{ChatColor.Red}数据库写入失败,分手未完成");
                    }

                    if (IsValidClient(currentTarget))
                    {
                        Chat(currentTarget!, $"{ChatColor.Red}数据库写入失败,分手未完成");
                    }

                    return;
                }

                if (IsValidClient(currentClient) && IsValidClient(currentTarget))
                {
                    ChatAll($"{ChatColor.Purple}{currentClient!.Name} {ChatColor.White}和 {ChatColor.Purple}{currentTarget!.Name} {ChatColor.White}已和平分手,愿各自安好,再遇良人");
                }

                if (IsValidClient(currentClient))
                {
                    Chat(currentClient!, $"{ChatColor.Blue}分手已完成{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                    KickPlayer(currentClient!, clientSteamId, 5.0);
                }

                if (IsValidClient(currentTarget))
                {
                    Chat(currentTarget!, $"{ChatColor.Blue}分手已完成{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                    KickPlayer(currentTarget!, targetSteamId, 5.0);
                }
            });
        });
        return;
    }

    private void CancelBreakup(IGameClient client)
    {
        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user) || user.Status != Status.BreakingUp)
        {
            return;
        }

        user.Status = Status.Married;
        user.Num = 0;
        RestoreUserToMarried(user.SpouseSteamID);
        Chat(client, "操作取消");
        return;
    }
}
