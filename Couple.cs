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
    private const double DatabaseRetryIntervalSeconds = 60.0;
    private const string Tag = "CP";

    private readonly IConfiguration _configuration;
    private readonly IClientManager _clients;
    private readonly ILogger<Couple> _logger;
    private readonly IModSharp _modSharp;
    private readonly ISharedSystem _sharedSystem;

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
        _sharedSystem = sharedSystem;
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

        _statusTimer = _modSharp.PushTimer(TimerUpdate, 1.0, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
        _databaseRetryTimer = _modSharp.PushTimer(TryReconnectDatabase, DatabaseRetryIntervalSeconds, GameTimerFlags.Repeatable);

        return true;
    }

    public void PostInit()
    {
        TryResolveWorldTextMenu();
        TryReconnectDatabase();
    }

    public void Shutdown()
    {
        _isLoaded = false;

        StopTimer(ref _statusTimer);
        StopTimer(ref _databaseRetryTimer);

        _clients.RemoveCommandCallback("cp", PartnerInviteRequest);
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);

        _users.Clear();
    }

    public void OnAllModulesLoaded()
    {
        TryResolveWorldTextMenu(logFailure: true);
    }

    public void OnLibraryConnected(string name)
    {
        TryResolveWorldTextMenu();
    }

    public void OnLibraryDisconnect(string name)
    {
    }

    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;
    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;
}
