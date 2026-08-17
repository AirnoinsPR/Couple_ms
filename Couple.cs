using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;

namespace Ins.Couple;

public sealed partial class Couple : IModSharpModule, IClientListener, IGameListener
{
    private const double DatabaseRetryIntervalSeconds = 10.0;
    private const string Tag = "CP";

    private readonly IConfiguration _configuration;
    private readonly IClientManager _clients;
    private readonly ILogger<Couple> _logger;
    private readonly IModSharp _modSharp;

    private readonly Dictionary<ulong, User> _users = new();

    private string _connectionString = string.Empty;
    private bool _isDbConnected;
    private bool _isLoaded;
    private Guid _databaseRetryTimer;
    private Guid _statusTimer;

    public Couple(ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload)
    {
        _configuration = coreConfiguration;
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<Couple>();
        _modSharp = sharedSystem.GetModSharp();
        _clients = sharedSystem.GetClientManager();
    }

    public string DisplayName => "Couple";
    public string DisplayAuthor => "Airnoins";

    public bool Init()
    {
        _isLoaded = true;

        _clients.InstallClientListener(this);
        _modSharp.InstallGameListener(this);

        _clients.InstallCommandCallback("cp", PartnerInviteRequest);
        _clients.InstallCommandCallback("bc", BreakUpCouple);
        _clients.InstallCommandCallback("cptp", TeleportCouple);
        _clients.InstallCommandCallback("cptp2", TeleportCoupleToMe);

        _statusTimer = _modSharp.PushTimer(TimerUpdate, 1.0, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
        _databaseRetryTimer = _modSharp.PushTimer(TryReconnectDatabase, DatabaseRetryIntervalSeconds, GameTimerFlags.Repeatable);
        _modSharp.InvokeFrameAction(TryReconnectDatabase);

        return true;
    }

    public void PostInit()
    {
        TryReconnectDatabase();
    }

    public void Shutdown()
    {
        _isLoaded = false;

        StopTimer(ref _statusTimer);
        StopTimer(ref _databaseRetryTimer);

        _clients.RemoveCommandCallback("cp", PartnerInviteRequest);
        _clients.RemoveCommandCallback("bc", BreakUpCouple);
        _clients.RemoveCommandCallback("cptp", TeleportCouple);
        _clients.RemoveCommandCallback("cptp2", TeleportCoupleToMe);
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);

        _users.Clear();
    }

    public void OnAllModulesLoaded()
    {
        TryReconnectDatabase();
    }

    public void OnLibraryConnected(string name)
    {
    }

    public void OnLibraryDisconnect(string name)
    {
    }

    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;
    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;
}
