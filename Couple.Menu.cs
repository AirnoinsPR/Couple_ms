using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using WorldTextMenu.Shared;

namespace Ins.Couple;

public sealed partial class Couple
{
    private const string WorldTextMenuAssemblyName = "WorldTextMenu";

    private IModSharpModuleInterface<IWorldTextMenu>? _worldTextMenu;

    private void ShowCoupleMenu(IGameClient client)
    {
        if (!TryGetWorldTextMenu(out var worldTextMenu))
        {
            Chat(client, $"{ChatColor.Red}CP菜单暂不可用,请稍后再试");
            return;
        }

        ulong steamId = client.SteamId.AsPrimitive();
        if (!_users.TryGetValue(steamId, out var user))
        {
            Chat(client, $"{ChatColor.Red}正在加载你的CP数据,请稍后再试");
            return;
        }

        var menu = BuildCoupleMenu(client, user);
        if (!worldTextMenu.Show(client, menu))
        {
            Chat(client, $"{ChatColor.Red}CP菜单打开失败");
        }
    }

    private WorldMenu BuildCoupleMenu(IGameClient client, User user)
    {
        var menu = new WorldMenu(DisplayName, "CP菜单")
        {
            DurationSeconds = 30
        };

        switch (user.Status)
        {
            case Status.Married:
                AddMarriedMenuItems(menu, user);
                break;
            case Status.Requester:
            case Status.PendingProposal:
            case Status.Proposed:
            case Status.BreakingUp:
            case Status.ReqSuccess:
                menu.AddDisabledItem("当前有待处理操作");
                menu.AddDisabledItem("请先完成或等待超时");
                break;
            default:
                menu.AddDisabledItem("当前状态: 未绑定伴侣");
                menu.AddItem("发起求婚", controller => controller.Next(BuildProposalMenu(client, menu)))
                    .PostSelectAction = WorldMenuPostSelectAction.Nothing;
                break;
        }

        menu.AddItem("关闭", controller => controller.Exit());
        return menu;
    }

    private bool ShowProposalResponseMenu(IGameClient client, IGameClient requester)
    {
        if (!TryGetWorldTextMenu(out var worldTextMenu))
        {
            return false;
        }

        var menu = new WorldMenu(DisplayName, "求婚请求")
        {
            DurationSeconds = 10
        };

        menu.AddDisabledItem($"{requester.Name} 向你发起了求婚");
        menu.AddItem("同意求婚", controller => AcceptProposal(controller.Client));
        menu.AddItem("拒绝求婚", controller => RejectProposal(controller.Client));

        return worldTextMenu.ShowModal(client, menu);
    }

    private bool ShowBreakupConfirmMenu(IGameClient client, User user)
    {
        if (!TryGetWorldTextMenu(out var worldTextMenu))
        {
            return false;
        }

        var spouse = GetClientBySteamId(user.SpouseSteamID);
        string spouseName = IsValidClient(spouse) ? spouse!.Name : user.SpouseTitle;

        var menu = new WorldMenu(DisplayName, "分手确认")
        {
            DurationSeconds = 10
        };

        menu.AddDisabledItem($"确认和 {spouseName} 分手?");
        menu.AddItem("确认分手", controller => ConfirmBreakup(controller.Client));
        menu.AddItem("取消", controller => CancelBreakup(controller.Client));

        return worldTextMenu.ShowModal(client, menu);
    }

    private WorldMenu BuildProposalMenu(IGameClient client, WorldMenu previousMenu)
    {
        var menu = new WorldMenu(DisplayName, "选择求婚对象")
        {
            PreviousMenu = previousMenu,
            DurationSeconds = 30
        };

        bool hasAnyTarget = false;
        ulong steamId = client.SteamId.AsPrimitive();

        foreach (var target in _clients.GetGameClients(true))
        {
            if (!IsValidClient(target) || target.SteamId.AsPrimitive() == steamId)
            {
                continue;
            }

            hasAnyTarget = true;
            ulong targetSteamId = target.SteamId.AsPrimitive();
            string targetName = target.Name;

            if (!_users.TryGetValue(targetSteamId, out var targetUser))
            {
                menu.AddDisabledItem($"{targetName}(数据加载中)");
                continue;
            }

            switch (targetUser.Status)
            {
                case Status.Married:
                    menu.AddDisabledItem($"{targetName}(已有伴侣)");
                    break;
                case Status.Requester:
                case Status.PendingProposal:
                case Status.Proposed:
                case Status.BreakingUp:
                case Status.ReqSuccess:
                    menu.AddDisabledItem($"{targetName}(有待处理操作)");
                    break;
                default:
                    menu.AddItem(targetName, controller =>
                    {
                        StartProposal(controller.Client, targetSteamId);
                        controller.Exit();
                    });
                    break;
            }
        }

        if (!hasAnyTarget)
        {
            menu.AddDisabledItem("暂无可求婚的在线玩家");
        }

        return menu;
    }

    private void AddMarriedMenuItems(WorldMenu menu, User user)
    {
        var spouse = GetClientBySteamId(user.SpouseSteamID);
        bool spouseOnline = IsValidClient(spouse);

        menu.AddDisabledItem($"当前状态: 已有伴侣");
        menu.AddDisabledItem(spouseOnline ? $"伴侣在线: {spouse!.Name}" : "伴侣当前不在线");

        if (spouseOnline)
        {
            menu.AddItem("传送到伴侣", controller =>
            {
                TryTeleportCouple(controller.Client, toMe: false);
                controller.Exit();
            });

            menu.AddItem("让伴侣传送到我", controller =>
            {
                TryTeleportCouple(controller.Client, toMe: true);
                controller.Exit();
            });
        }
        else
        {
            menu.AddDisabledItem("传送功能需要伴侣在线");
        }

        menu.AddItem("发起分手确认", controller =>
        {
            StartBreakup(controller.Client);
        }).PostSelectAction = WorldMenuPostSelectAction.Nothing;
    }

    private void TryResolveWorldTextMenu(bool logFailure = false)
    {
        if (_worldTextMenu?.Instance is not null)
        {
            return;
        }

        _worldTextMenu = _sharedSystem.GetSharpModuleManager()
                                      .GetOptionalSharpModuleInterface<IWorldTextMenu>(IWorldTextMenu.Identity);

        if (_worldTextMenu is null && logFailure)
        {
            _logger.LogWarning("Failed to get WorldTextMenu. Do you have '{AssemblyName}' installed?",
                WorldTextMenuAssemblyName);
        }
    }

    private bool TryGetWorldTextMenu(out IWorldTextMenu worldTextMenu)
    {
        TryResolveWorldTextMenu();

        if (_worldTextMenu?.Instance is { } instance)
        {
            worldTextMenu = instance;
            return true;
        }

        worldTextMenu = null!;
        return false;
    }
}
