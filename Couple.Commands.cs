using System.Threading.Tasks;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace Ins.Couple;

public sealed partial class Couple
{
    private void StartBreakup(IGameClient client)
    {
        if (!IsValidClient(client) || !_isDbConnected)
        {
            if (IsValidClient(client))
            {
                Chat(client, $"{ChatColor.Red}数据库暂不可用,请稍后再试");
            }

            return;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.TryGetValue(steamId, out var user))
        {
            return;
        }

        if (user.Status != Status.Married)
        {
            Chat(client, "你还没有伴侣呢");
            return;
        }

        user.Status = Status.BreakingUp;
        user.Num = 0;

        if (_users.TryGetValue(user.SpouseSteamID, out var targetUser))
        {
            targetUser.Status = Status.BreakingUp;
            targetUser.Num = 0;
        }

        if (!ShowBreakupConfirmMenu(client, user))
        {
            user.Status = Status.Married;
            user.Num = 0;
            RestoreUserToMarried(user.SpouseSteamID);
            Chat(client, $"{ChatColor.Red}CP菜单打开失败,分手已取消");
        }

        return;
    }

    private void TryTeleportCouple(IGameClient client, bool toMe)
    {
        if (!IsValidClient(client))
        {
            return;
        }

        if (!_users.TryGetValue(client.SteamId.AsPrimitive(), out var user))
        {
            return;
        }

        if (user.SpouseSteamID == 0)
        {
            Chat(client, "你目前还没有伴侣...");
            return;
        }

        var target = GetClientBySteamId(user.SpouseSteamID);
        if (!IsValidClient(target))
        {
            Chat(client, "伴侣当前不在线");
            return;
        }

        if (!CanTeleportCouple(client, target!, out string teleportError))
        {
            Chat(client, $"{ChatColor.Red}{teleportError}");
            return;
        }

        if (toMe)
        {
            ChatAll($"{ChatColor.Pink}{target!.Name} {ChatColor.White}已传送至 {ChatColor.Pink}{client.Name}");
            TeleportPlayer(client, target, true);
        }
        else
        {
            ChatAll($"{ChatColor.Pink}{client.Name} {ChatColor.White}已传送至 {ChatColor.Pink}{target!.Name}");
            TeleportPlayer(client, target);
        }

        return;
    }

    private ECommandAction PartnerInviteRequest(IGameClient client, StringCommand command)
    {
        _ = command;

        if (!IsValidClient(client) || !_isDbConnected)
        {
            if (IsValidClient(client))
            {
                Chat(client, $"{ChatColor.Red}数据库暂不可用,请稍后再试");
            }

            return ECommandAction.Handled;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.ContainsKey(steamId))
        {
            Chat(client, $"{ChatColor.Red}正在加载你的CP数据,请稍后再试");
            return ECommandAction.Handled;
        }

        ShowCoupleMenu(client);
        return ECommandAction.Handled;
    }

    private void StartProposal(IGameClient client, ulong targetSteamId)
    {
        if (!IsValidClient(client) || !_isDbConnected)
        {
            if (IsValidClient(client))
            {
                Chat(client, $"{ChatColor.Red}数据库暂不可用,请稍后再试");
            }

            return;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.TryGetValue(steamId, out var user))
        {
            return;
        }

        switch (user.Status)
        {
            case Status.Requester:
            case Status.ReqSuccess:
                Chat(client, $"{ChatColor.Red}请勿频繁请求");
                return;
            case Status.Proposed:
                Chat(client, $"{ChatColor.Red}你有一个请求未处理");
                return;
            case Status.Married:
                Chat(client, "你已经有一个伴侣了,不要太花心");
                return;
        }

        if (steamId == targetSteamId)
        {
            Chat(client, $"{ChatColor.Red}并不可以自恋");
            return;
        }

        var target = GetClientBySteamId(targetSteamId);
        if (!IsValidClient(target))
        {
            Chat(client, $"{ChatColor.Red}无效ID或目标离线");
            return;
        }

        if (!_users.TryGetValue(targetSteamId, out var targetUser))
        {
            Chat(client, "未知错误,请联系管理员");
            return;
        }

        switch (targetUser.Status)
        {
            case Status.Requester:
            case Status.Proposed:
            case Status.ReqSuccess:
            case Status.PendingProposal:
                Chat(client, "对方当前有待处理请求,无法发起求婚");
                return;
            case Status.Married:
                Chat(client, "对方已经有心上人了,无法发起求婚");
                return;
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
                if (!_isLoaded)
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

                if (!IsValidClient(client) || !IsValidClient(target))
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    if (IsValidClient(client))
                    {
                        Chat(client, $"{ChatColor.Red}对方已离线或不可用,求婚已取消");
                    }

                    return;
                }

                if (!_isDbConnected)
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    Chat(client, $"{ChatColor.Red}数据库暂不可用,求婚已取消");
                    return;
                }

                if (!canMe)
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    Chat(client, "你当前处于分手冷静期,无法进行求婚");
                    return;
                }

                if (!canOther)
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    Chat(client, "对方当前处于分手冷静期,无法进行求婚");
                    return;
                }

                currentTargetUser.Status = Status.Proposed;
                currentTargetUser.Num = 0;

                if (!ShowProposalResponseMenu(target!, client))
                {
                    ResetPendingProposal(currentUser, currentTargetUser);
                    Chat(client, $"{ChatColor.Red}对方CP菜单打开失败,求婚已取消");
                    Chat(target!, $"{ChatColor.Red}CP菜单打开失败,求婚已取消");
                    return;
                }

                Chat(client, $"已向 {ChatColor.Pink}{target!.Name} {ChatColor.White}发起求婚");
            });
        });

        return;
    }
}
