using System.Threading.Tasks;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace Ins.Couple;

public sealed partial class Couple
{
    private ECommandAction BreakUpCouple(IGameClient client, StringCommand command)
    {
        if (!IsValidClient(client) || !_isDbConnected)
        {
            return ECommandAction.Handled;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.TryGetValue(steamId, out var user))
        {
            return ECommandAction.Handled;
        }

        if (user.Status != Status.Married)
        {
            Reply(client, "你还没有老婆呢");
            return ECommandAction.Handled;
        }

        var target = GetClientBySteamId(user.SpouseSteamID);
        if (!IsValidClient(target))
        {
            Reply(client, OfflineSpouseMessage(user, "目前不在线,无法发起分手"));
            return ECommandAction.Handled;
        }

        if (!_users.TryGetValue(user.SpouseSteamID, out var targetUser))
        {
            Reply(client, "未知错误,请联系管理员");
            return ECommandAction.Handled;
        }

        user.Status = Status.BreakingUp;

        const string separator = "---------------------------------------------------------------------";
        string requestMessage = $"你正在发起分手请求,请在10秒内输入 {ChatColor.Green}yes{ChatColor.White}|{ChatColor.Red}no {ChatColor.White}来确认操作";
        Chat(client, separator, usePrefix: false);
        Reply(client, requestMessage);
        Reply(client, requestMessage);
        Reply(client, requestMessage);
        Chat(client, separator, usePrefix: false);

        return ECommandAction.Handled;
    }

    private ECommandAction TeleportCouple(IGameClient client, StringCommand command)
    {
        if (!IsValidClient(client))
        {
            return ECommandAction.Handled;
        }

        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user))
        {
            return ECommandAction.Handled;
        }

        if (user.SpouseSteamID == 0)
        {
            Reply(client, "单身狗用什么cptp");
            return ECommandAction.Handled;
        }

        var target = GetClientBySteamId(user.SpouseSteamID);
        if (!IsValidClient(target))
        {
            Reply(client, OfflineSpouseMessage(user, "目前不在线哦"));
            return ECommandAction.Handled;
        }

        ChatAll($"{ChatColor.Purple}{client.Name} {ChatColor.White}已传送至 {ChatColor.Purple}{target!.Name}");
        TeleportPlayer(client, target);
        return ECommandAction.Handled;
    }

    private ECommandAction TeleportCoupleToMe(IGameClient client, StringCommand command)
    {
        if (!IsValidClient(client))
        {
            return ECommandAction.Handled;
        }

        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user))
        {
            return ECommandAction.Handled;
        }

        if (user.SpouseSteamID == 0)
        {
            Reply(client, "单身狗用什么cptp");
            return ECommandAction.Handled;
        }

        var target = GetClientBySteamId(user.SpouseSteamID);
        if (!IsValidClient(target))
        {
            Reply(client, OfflineSpouseMessage(user, "目前不在线哦"));
            return ECommandAction.Handled;
        }

        ChatAll($"{ChatColor.Purple}{target!.Name} {ChatColor.White}已传送至 {ChatColor.Purple}{client.Name}");
        TeleportPlayer(client, target, true);
        return ECommandAction.Handled;
    }

    private ECommandAction PartnerInviteRequest(IGameClient client, StringCommand command)
    {
        if (!IsValidClient(client) || !_isDbConnected)
        {
            return ECommandAction.Handled;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.TryGetValue(steamId, out var user))
        {
            return ECommandAction.Handled;
        }

        switch (user.Status)
        {
            case Status.Requester:
            case Status.ReqSuccess:
                Reply(client, $"{ChatColor.Red}请勿频繁请求");
                return ECommandAction.Handled;
            case Status.Proposed:
                Reply(client, $"{ChatColor.Red}你有一个请求未处理");
                return ECommandAction.Handled;
            case Status.Married:
                Reply(client, "你已经有一个伴侣了,不要太花心");
                return ECommandAction.Handled;
        }

        if (command.ArgCount < 1 || !ulong.TryParse(command.GetArg(1), out ulong targetSteamId) || targetSteamId == 0)
        {
            Reply(client, $"用法: {ChatColor.Green}!cp <steamid64>");
            return ECommandAction.Handled;
        }

        if (steamId == targetSteamId)
        {
            Reply(client, $"{ChatColor.Red}并不可以自恋");
            return ECommandAction.Handled;
        }

        var target = GetClientBySteamId(targetSteamId);
        if (!IsValidClient(target))
        {
            Reply(client, $"{ChatColor.Red}无效ID或目标离线");
            return ECommandAction.Handled;
        }

        if (!_users.TryGetValue(targetSteamId, out var targetUser))
        {
            Reply(client, "未知错误,请联系管理员");
            return ECommandAction.Handled;
        }

        switch (targetUser.Status)
        {
            case Status.Requester:
            case Status.Proposed:
            case Status.ReqSuccess:
            case Status.PendingProposal:
                Reply(client, "对方当前处于思考状态,无法进行求婚");
                return ECommandAction.Handled;
            case Status.Married:
                Reply(client, "对方已经有心上人了,无法进行求婚");
                return ECommandAction.Handled;
        }

        user.Status = Status.Requester;
        user.Num = 0;
        targetUser.Status = Status.PendingProposal;
        targetUser.RequesterSteamID = steamId;
        targetUser.Num = 0;

        Task.Run(async () =>
        {
            bool canMe = await CanMarryAgainAsync(user.SteamID);
            bool canOther = await CanMarryAgainAsync(targetUser.SteamID);

            _modSharp.InvokeFrameAction(() =>
            {
                if (!_isLoaded || !client.IsValid || target is not { IsValid: true })
                {
                    return;
                }

                if (!_users.TryGetValue(steamId, out var currentUser)
                    || !_users.TryGetValue(targetSteamId, out var currentTargetUser)
                    || currentUser.Status != Status.Requester
                    || currentTargetUser.Status != Status.PendingProposal
                    || currentTargetUser.RequesterSteamID != steamId)
                {
                    return;
                }

                if (!canMe)
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    Reply(client, "你当前处于分手冷静期,无法进行求婚");
                    return;
                }

                if (!canOther)
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    Reply(client, "对方当前处于分手冷静期,无法进行求婚");
                    return;
                }

                currentTargetUser.Status = Status.Proposed;
                currentTargetUser.Num = 0;

                Reply(client, $"你正在向 {ChatColor.Pink}{target.Name} {ChatColor.White}求婚");
                Chat(target, "---------------------------------------------------------------------", usePrefix: false);
                string requestMessage = $"{ChatColor.Blue}{client.Name} {ChatColor.White}向你发起了求婚,请在10秒内输入 {ChatColor.Green}yes{ChatColor.White}|{ChatColor.Red}no {ChatColor.White}来进行处理";
                Chat(target, requestMessage);
                Chat(target, requestMessage);
                Chat(target, requestMessage);
                Chat(target, "---------------------------------------------------------------------", usePrefix: false);
            });
        });

        return ECommandAction.Handled;
    }
}
