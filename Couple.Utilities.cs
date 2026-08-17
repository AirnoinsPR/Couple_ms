using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

namespace Ins.Couple;

public sealed partial class Couple
{
    private static readonly string ChatPrefix = $"{ChatColor.Purple}[{Tag}]{ChatColor.White} ";

    private static bool IsValidClient(IGameClient? client)
    {
        if (client is not { IsValid: true, IsInGame: true, IsConnected: true, IsFakeClient: false, IsHltv: false })
        {
            return false;
        }

        if (client.SteamId.AsPrimitive() == 0)
        {
            return false;
        }

        return client.GetPlayerController() is
        {
            ConnectedState: PlayerConnectedState.PlayerConnected,
            IsFakeClient: false,
            IsHltv: false
        };
    }

    private IGameClient? GetClientBySteamId(ulong steamId)
    {
        return steamId == 0 ? null : _clients.GetGameClient(steamId);
    }

    private static bool IsSpouseDataValid((ulong? spouseSteamId, string? spouseGender) data)
    {
        return data.spouseSteamId.HasValue && !string.IsNullOrEmpty(data.spouseGender);
    }

    private static bool CanPersistPlayer(ulong steamId)
    {
        return steamId != 0;
    }

    private static bool CanTeleportCouple(IGameClient client, IGameClient target, out string errorMessage)
    {
        var clientPawn = client.GetPlayerController()?.GetPlayerPawn();
        if (clientPawn is not { IsValidEntity: true, IsAlive: true })
        {
            errorMessage = "你死亡时无法传送";
            return false;
        }

        var targetPawn = target.GetPlayerController()?.GetPlayerPawn();
        if (targetPawn is not { IsValidEntity: true, IsAlive: true })
        {
            errorMessage = "对方死亡时无法传送";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static void ResetPendingProposal(User requester, User proposed)
    {
        requester.Status = Status.None;
        requester.RequesterSteamID = 0;
        requester.Num = 0;

        proposed.Status = Status.None;
        proposed.RequesterSteamID = 0;
        proposed.Num = 0;
    }

    private void ResetRequester(ulong requesterSteamId)
    {
        if (requesterSteamId == 0 || !_users.TryGetValue(requesterSteamId, out var requester))
        {
            return;
        }

        requester.Status = Status.None;
        requester.RequesterSteamID = 0;
        requester.Num = 0;
    }

    private void ResetUsersToNone(ulong steamId0, ulong steamId1)
    {
        ResetUserToNone(steamId0);
        ResetUserToNone(steamId1);
    }

    private void ResetUserToNone(ulong steamId)
    {
        if (!_users.TryGetValue(steamId, out var user))
        {
            return;
        }

        user.Status = Status.None;
        user.RequesterSteamID = 0;
        user.Num = 0;
    }

    private void RestoreUsersToMarried(ulong steamId0, ulong steamId1)
    {
        RestoreUserToMarried(steamId0);
        RestoreUserToMarried(steamId1);
    }

    private void RestoreUserToMarried(ulong steamId)
    {
        if (!_users.TryGetValue(steamId, out var user))
        {
            return;
        }

        user.Status = Status.Married;
        user.Num = 0;
    }

    private void TeleportPlayer(IGameClient client, IGameClient target, bool toMe = false)
    {
        var clientPawn = client.GetPlayerController()?.GetPlayerPawn();
        var targetPawn = target.GetPlayerController()?.GetPlayerPawn();
        if (clientPawn is null || targetPawn is null || !clientPawn.IsValidEntity || !targetPawn.IsValidEntity)
        {
            return;
        }

        var clientEyeAngles = clientPawn.GetEyeAngles();
        var targetEyeAngles = targetPawn.GetEyeAngles();

        if (!toMe)
        {
            clientPawn.Teleport(targetPawn.GetAbsOrigin(), clientEyeAngles, targetPawn.GetAbsVelocity());
        }
        else
        {
            targetPawn.Teleport(clientPawn.GetAbsOrigin(), targetEyeAngles, clientPawn.GetAbsVelocity());
        }
    }

    private void NotifySpousePresence(ulong steamId)
    {
        if (!_users.TryGetValue(steamId, out var user) || user.Status != Status.Married)
        {
            return;
        }

        var client = GetClientBySteamId(steamId);
        if (!IsValidClient(client))
        {
            return;
        }

        Task.Run(async () => await UpdateLastSeenAsync(steamId, user.CPSide));

        var spouse = GetClientBySteamId(user.SpouseSteamID);
        if (!IsValidClient(spouse))
        {
            NotifyOfflineSpouse(client!, user);
            return;
        }

        if (!_users.TryGetValue(user.SpouseSteamID, out var spouseUser))
        {
            return;
        }

        switch (user.CPSide)
        {
            case CPSide.Male:
                Chat(client!, $"你{ChatColor.Pink}{user.SpouseTitle}{ChatColor.White}目前在线哦,祝你们玩的愉快");
                Chat(spouse!, $"你{ChatColor.Blue}{spouseUser.SpouseTitle}{ChatColor.White}目前在线哦,祝你们玩的愉快");
                break;
            case CPSide.Female:
                Chat(client!, $"你{ChatColor.Blue}{user.SpouseTitle}{ChatColor.White}目前在线哦,祝你们玩的愉快");
                Chat(spouse!, $"你{ChatColor.Pink}{spouseUser.SpouseTitle}{ChatColor.White}目前在线哦,祝你们玩的愉快");
                break;
        }
    }

    private void NotifyOfflineSpouse(IGameClient client, User user)
    {
        Task.Run(async () =>
        {
            CPSide side = user.CPSide == CPSide.Female ? CPSide.Male : CPSide.Female;
            var lastSeen = await GetLastSeenAsync(user.SpouseSteamID, side);
            if (!lastSeen.HasValue)
            {
                return;
            }

            string formattedLastSeen = lastSeen.Value.ToString("yyyy年MM月dd日 HH:mm");
            _modSharp.InvokeFrameAction(() =>
            {
                switch (user.CPSide)
                {
                    case CPSide.Male:
                        Chat(client, $"你{ChatColor.Pink}{user.SpouseTitle}{ChatColor.White}目前不在线哦 上次在线日期为 {ChatColor.Green}{formattedLastSeen}");
                        break;
                    case CPSide.Female:
                        Chat(client, $"你{ChatColor.Blue}{user.SpouseTitle}{ChatColor.White}目前不在线哦 上次在线日期为 {ChatColor.Green}{formattedLastSeen}");
                        break;
                }
            });
        });
    }

    private string OfflineSpouseMessage(User user, string suffix)
    {
        string color = user.CPSide == CPSide.Male ? ChatColor.Pink : ChatColor.Blue;
        return $"你{color}{user.SpouseTitle}{ChatColor.White}{suffix}";
    }

    private void PushTimer(Action action, double delay)
    {
        _modSharp.PushTimer(action, delay, GameTimerFlags.StopOnMapEnd);
    }

    private void ScheduleDisconnect(IGameClient client, ulong steamId)
    {
        PushTimer(() =>
        {
            _users.Remove(steamId);

            if (client.IsValid)
            {
                _clients.KickClient(client, "Couple status changed", NetworkDisconnectionReason.Kicked);
            }
        }, 5.0);
    }

    private void StopTimer(ref Guid timer)
    {
        if (timer != Guid.Empty && _modSharp.IsValidTimer(timer))
        {
            _modSharp.StopTimer(timer);
        }

        timer = Guid.Empty;
    }

    private void Chat(IGameClient client, string message, bool usePrefix = true)
    {
        client.Print(HudPrintChannel.Chat, usePrefix ? ChatPrefix + message : message, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private void Reply(IGameClient client, string message)
    {
        Chat(client, message);
    }

    private void ChatAll(string message)
    {
        _modSharp.PrintToChatAll(ChatPrefix + message);
    }

    private void Echo(string message)
    {
        _logger.LogInformation("{Message}", message);
        _modSharp.LogMessage(message);
    }

    private void EchoWarning(string message)
    {
        _logger.LogWarning("{Message}", message);
        _modSharp.LogWarning(message);
    }
}
