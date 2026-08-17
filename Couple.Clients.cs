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

        if (!_isDbConnected || !_users.TryGetValue(steamId, out var user) || user.Status != Status.Married)
        {
            _users.Remove(steamId);
            return;
        }

        Task.Run(async () => await UpdateLastSeenAsync(steamId, user.CPSide));
        _users.Remove(steamId);
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
                ChatAll($"{ChatColor.Blue}{requester!.Name} {ChatColor.White}向 {ChatColor.Pink}{client.Name} {ChatColor.White}求婚成功,恭喜他们结成连理,幸福美满");
                _ = AddCoupleAsync(client.SteamId.AsPrimitive(), requester.SteamId.AsPrimitive());
                user.Status = Status.ReqSuccess;
                requesterUser.Status = Status.ReqSuccess;
                Chat(client, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                Chat(requester, $"{ChatColor.Pink}新婚快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");

                Task.Run(async () =>
                {
                    await UpdateLastSeenAsync(client.SteamId.AsPrimitive(), CPSide.Female);
                    await UpdateLastSeenAsync(requester.SteamId.AsPrimitive(), CPSide.Male);
                });

                SchedulePairDisconnect(client, requester);
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

                ChatAll($"{ChatColor.Purple}{client.Name} {ChatColor.White}和 {ChatColor.Purple}{target!.Name} {ChatColor.White}已和平分手,愿各自安好,再遇良人");
                Task.Run(async () =>
                {
                    await UpdateLastSeenAsync(client.SteamId.AsPrimitive(), user.CPSide);
                    await UpdateLastSeenAsync(target.SteamId.AsPrimitive(), targetUser.CPSide);
                    await BreakUpCoupleAsync(client.SteamId.AsPrimitive(), target.SteamId.AsPrimitive());
                });

                user.Status = Status.ReqSuccess;
                targetUser.Status = Status.ReqSuccess;
                Chat(client, $"{ChatColor.Blue}分手快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                Chat(target, $"{ChatColor.Blue}分手快乐{ChatColor.White},将在5秒后自动断开,请重新连接至服务器");
                SchedulePairDisconnect(client, target);
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
