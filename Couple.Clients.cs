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

        ulong steamId = client.SteamId.AsPrimitive();

        Task.Run(async () =>
        {
            var data = await GetSpouseSteamIdAndGenderAsync(steamId);

            _modSharp.InvokeFrameAction(() =>
            {
                if (!_isLoaded || !client.IsValid)
                {
                    return;
                }

                _users[steamId] = BuildUser(steamId, data);
            });
        });

        PushTimer(() => NotifySpousePresence(steamId), 7.0);
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

        Task.Run(async () => await UpdateLastSeenAsync(steamId, user.CPSide));
        _users.Remove(steamId);
    }

    private void ClearPendingStateOnDisconnect(ulong steamId, User user)
    {
        if (user.Status is Status.Proposed or Status.PendingProposal)
        {
            ResetRequester(user.RequesterSteamID);
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

    public ECommandAction OnClientSayCommand(IGameClient client,
        bool teamOnly,
        bool isCommand,
        string commandName,
        string message)
    {
        if (!IsValidClient(client) || string.IsNullOrWhiteSpace(message) || !_isDbConnected)
        {
            return ECommandAction.Skipped;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.TryGetValue(steamId, out var user))
        {
            return ECommandAction.Skipped;
        }

        string msg = message.Trim().ToLowerInvariant();

        return user.Status switch
        {
            Status.Proposed => HandleProposalResponse(client, user, msg),
            Status.BreakingUp => HandleBreakupResponse(client, user, msg),
            _ => ECommandAction.Skipped
        };
    }

    public void OnGameDeactivate()
    {
        _users.Clear();
    }

    private User BuildUser(ulong steamId, (ulong? spouseSteamId, string? spouseGender) data)
    {
        if (!IsSpouseDataValid(data))
        {
            return new User
            {
                SteamID = steamId
            };
        }

        CPSide side = data.spouseGender switch
        {
            "老婆" => CPSide.Male,
            "老公" => CPSide.Female,
            _ => CPSide.Female
        };

        return new User
        {
            SteamID = steamId,
            SpouseSteamID = data.spouseSteamId!.Value,
            SpouseTitle = data.spouseGender!,
            Status = Status.Married,
            CPSide = side
        };
    }

    private ECommandAction HandleProposalResponse(IGameClient client, User user, string message)
    {
        var requester = GetClientBySteamId(user.RequesterSteamID);
        if (!IsValidClient(requester))
        {
            Chat(client, "目标失效或离线");
            return ECommandAction.Handled;
        }

        if (!_users.TryGetValue(user.RequesterSteamID, out var requesterUser))
        {
            Chat(client, "目标失效或离线");
            return ECommandAction.Handled;
        }

        switch (message)
        {
            case "yes":
                ulong proposedSteamId = client.SteamId.AsPrimitive();
                ulong requesterSteamId = requester!.SteamId.AsPrimitive();
                user.Status = Status.ReqSuccess;
                requesterUser.Status = Status.ReqSuccess;

                Task.Run(async () =>
                {
                    bool added = await AddCoupleAsync(proposedSteamId, requesterSteamId);
                    if (added)
                    {
                        await UpdateLastSeenAsync(proposedSteamId, CPSide.Female);
                        await UpdateLastSeenAsync(requesterSteamId, CPSide.Male);
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
                                Chat(proposedClient!, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                                ScheduleDisconnect(proposedClient!, proposedSteamId);
                            }

                            if (IsValidClient(requesterClient))
                            {
                                Chat(requesterClient!, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                                ScheduleDisconnect(requesterClient!, requesterSteamId);
                            }

                            return;
                        }

                        ChatAll($"{ChatColor.Blue}{requesterClient!.Name} {ChatColor.White}向 {ChatColor.Pink}{proposedClient!.Name} {ChatColor.White}求婚成功,恭喜他们结成连理,幸福美满");
                        Chat(proposedClient, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                        Chat(requesterClient, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                        SchedulePairDisconnect(proposedClient, requesterClient);
                    });
                });
                return ECommandAction.Handled;
            case "no":
                ChatAll($"{ChatColor.Blue}{requester!.Name} {ChatColor.White}向 {ChatColor.Pink}{client.Name} {ChatColor.White}求婚被拒绝,大家快笑他");
                user.Status = Status.None;
                user.Num = 0;
                user.RequesterSteamID = 0;
                requesterUser.Status = Status.None;
                return ECommandAction.Handled;
            default:
                return ECommandAction.Skipped;
        }
    }

    private ECommandAction HandleBreakupResponse(IGameClient client, User user, string message)
    {
        switch (message)
        {
            case "yes":
                var target = GetClientBySteamId(user.SpouseSteamID);
                if (!IsValidClient(target))
                {
                    user.Status = Status.Married;
                    Chat(client, $"{ChatColor.Red}目标离线");
                    return ECommandAction.Handled;
                }

                if (!_users.TryGetValue(user.SpouseSteamID, out var targetUser))
                {
                    user.Status = Status.Married;
                    Chat(client, "未知错误,请联系管理员");
                    return ECommandAction.Handled;
                }

                ulong clientSteamId = client.SteamId.AsPrimitive();
                ulong targetSteamId = target!.SteamId.AsPrimitive();
                CPSide clientSide = user.CPSide;
                CPSide targetSide = targetUser.CPSide;
                user.Status = Status.ReqSuccess;
                targetUser.Status = Status.ReqSuccess;

                Task.Run(async () =>
                {
                    bool brokenUp = await BreakUpCoupleAsync(clientSteamId, targetSteamId);
                    if (brokenUp)
                    {
                        await UpdateLastSeenAsync(clientSteamId, clientSide);
                        await UpdateLastSeenAsync(targetSteamId, targetSide);
                    }

                    _modSharp.InvokeFrameAction(() =>
                    {
                        var currentClient = GetClientBySteamId(clientSteamId);
                        var currentTarget = GetClientBySteamId(targetSteamId);

                        if (!brokenUp)
                        {
                            RestoreUsersToMarried(clientSteamId, targetSteamId);

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

                        if (!IsValidClient(currentClient) || !IsValidClient(currentTarget))
                        {
                            if (IsValidClient(currentClient))
                            {
                                Chat(currentClient!, $"{ChatColor.Blue}分手快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                                ScheduleDisconnect(currentClient!, clientSteamId);
                            }

                            if (IsValidClient(currentTarget))
                            {
                                Chat(currentTarget!, $"{ChatColor.Blue}分手快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                                ScheduleDisconnect(currentTarget!, targetSteamId);
                            }

                            return;
                        }

                        ChatAll($"{ChatColor.Purple}{currentClient!.Name} {ChatColor.White}和 {ChatColor.Purple}{currentTarget!.Name} {ChatColor.White}已和平分手,愿各自安好,再遇良人");
                        Chat(currentClient, $"{ChatColor.Blue}分手快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                        Chat(currentTarget, $"{ChatColor.Blue}分手快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                        SchedulePairDisconnect(currentClient, currentTarget);
                    });
                });
                return ECommandAction.Handled;
            case "no":
                user.Status = Status.Married;
                user.Num = 0;
                Chat(client, "操作取消");
                return ECommandAction.Handled;
            default:
                return ECommandAction.Skipped;
        }
    }

    private void SchedulePairDisconnect(IGameClient client, IGameClient target)
    {
        PushTimer(() =>
        {
            ulong clientSteamId = client.SteamId.AsPrimitive();
            ulong targetSteamId = target.SteamId.AsPrimitive();

            _users.Remove(clientSteamId);
            _users.Remove(targetSteamId);

            if (client.IsValid)
            {
                _clients.KickClient(client, "Couple status changed", NetworkDisconnectionReason.Kicked);
            }

            if (target.IsValid)
            {
                _clients.KickClient(target, "Couple status changed", NetworkDisconnectionReason.Kicked);
            }
        }, 5.0);
    }
}
