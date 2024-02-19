// #define STEAM_PACKET_VERBOSE

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamKit2;
using RevoltSharp;
using System.Text.Json;
using System.Text;

namespace ScrapBot.Steam;

public class Options
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int MaxReconnectDelaySeconds { get; set; }
    public int PICSRefreshDelaySeconds { get; set; }
    public required List<Webhook> Webhooks { get; set; }
}

public class Service : IHostedService
{
    private Dictionary<uint, string> Apps = new() {
        {387990, "Scrap Mechanic"},
        {588870, "Scrap Mechanic Mod Tool"}
    };


    private readonly ILogger<Service> _logger;
    private readonly Options _options;

    private readonly SteamClient _steamClient;
    private readonly SteamApps _steamApps;
    private readonly SteamUser _steamUser;
    private readonly SteamFriends _steamFriends;
    private readonly CallbackManager _callbackManager;

    private readonly Timer _timer;

    private uint _lastChangeNumber;

    private bool _isAnon;

    private bool _isFirstConnection = true;
    private bool _isStopping;
    private int _reconnectAttempts;

    private readonly HttpClient _httpClient = new();

    public Service(ILogger<Service> logger, IOptions<Options> options)
    {
        _logger = logger;
        _options = options.Value;

        _timer = new Timer(TimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        CheckAnon();

        _steamClient = new SteamClient();
        _steamApps = _steamClient.GetHandler<SteamApps>()!;
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamFriends = _steamClient.GetHandler<SteamFriends>()!;

#if STEAM_PACKET_VERBOSE
        _steamClient.AddHandler(new VerboseHandler());
#endif

        _callbackManager = new CallbackManager(_steamClient);
        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnClientConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnClientDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnUserLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnUserLoggedOff);
        _callbackManager.Subscribe<SteamApps.PICSChangesCallback>(OnPICSChanges);
    }

    private void CheckAnon()
    {
        _isAnon = _options.Username is null || _options.Password is null;
    }

    private void TimerCallback(object? _)
    {
        _steamApps.PICSGetChangesSince(_lastChangeNumber, true, true);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting");

        var callbackTask = new Task(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(5));
            }
        }, cancellationToken, TaskCreationOptions.LongRunning);

        callbackTask.Start();

        _logger.LogInformation("Connecting");

        _steamClient.Connect();

        _logger.LogInformation("Started");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping");

        _isStopping = true;

        _steamClient.Disconnect();

        _logger.LogInformation("Stopped");

        return Task.CompletedTask;
    }

    private void OnClientConnected(SteamClient.ConnectedCallback callback)
    {
        _reconnectAttempts = 0;
        _logger.LogInformation("Client {}", _isFirstConnection ? "Connected" : "Reconnected");
        _isFirstConnection = false;

        CheckAnon();

        _logger.LogInformation("Logging On{}", _isAnon ? " Anonymously" : string.Empty);

        if (_isAnon)
        {
            _steamUser.LogOnAnonymous();
        }
        else
        {
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = _options.Username,
                Password = _options.Password
            });
        }
    }

    private async void OnClientDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("Disconnected");
        if (_isStopping) return;
        var seconds = (int)Math.Min(Math.Pow(2, _reconnectAttempts) * 15, _options.MaxReconnectDelaySeconds);
        var attempts = " (Attempt " + (_reconnectAttempts + 1) + ")";
        _logger.LogInformation("Reconnecting in " + seconds + " Second" + (seconds == 1 ? "" : "s") + attempts);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        _logger.LogInformation("Reconnecting" + attempts);
        _reconnectAttempts += 1;
        _steamClient.Connect();
    }

    private void OnUserLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            _logger.LogError($"Log On Failed: EResult.{callback.Result:G}({callback.Result:D})");
            return;
        }

        if (!_isAnon) _steamFriends.SetPersonaState(EPersonaState.Online);
        _logger.LogInformation("Logged On{}", _isAnon ? " Anonymously" : string.Empty);
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(_options.PICSRefreshDelaySeconds));
    }

    private void OnUserLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _logger.LogInformation("Logged Off");
    }

    private async void OnPICSChanges(SteamApps.PICSChangesCallback callback)
    {
        if (callback.LastChangeNumber == callback.CurrentChangeNumber) return;
        if (callback.CurrentChangeNumber > _lastChangeNumber) _lastChangeNumber = callback.CurrentChangeNumber;
        var apps = callback.AppChanges.Where(app => Apps.ContainsKey(app.Value.ID)).ToArray();
        if (apps.Length <= 0) return;
        foreach (var (_, app) in apps)
        {
            Apps.TryGetValue(app.ID, out var appName);
            foreach (var webhook in _options.Webhooks)
            {
                switch (webhook.type)
                {
                    case "discord":
                        {
                            var content = $"New SteamDB change detected! `{appName} ({app.ID})`  \nhttps://steamdb.info/app/{app.ID}/history/?changeid={app.ChangeNumber}\"";
                            using StringContent jsonContent = new(
                                JsonSerializer.Serialize(new
                                {
                                    content = content,
                                }),
                                Encoding.UTF8,
                                "application/json"
                            );
                            var res = await _httpClient.PostAsync(webhook.token, jsonContent);

                            res.Dispose();
                            break;
                        }
                    case "revolt":
                        {
                            var client = new RevoltClient(webhook.token, ClientMode.Http);
                            await client.StartAsync();

                            var content =
                $"New Steam PICS Change for App `{appName} ({app.ID})`  \nhttps://steamdb.info/app/{app.ID}/history/?changeid={app.ChangeNumber}";

                            if (webhook.revolt_chat == null)
                            {
                                Console.WriteLine("No channel for revolt webhook");
                                return;
                            }

                            var channel = await client.Rest.GetChannelAsync(webhook.revolt_chat);
                            if (channel == null)
                            {
                                Console.WriteLine("Channel for revolt not found");
                                return;
                            }
                            await channel.SendMessageAsync(content);
                            break;
                        }
                }
            }

        }

#if PICS_PRODUCT_INFO
        var productInfo = await _steamApps.PICSGetProductInfo(callback.AppChanges.Select(app => 
                                                                  new SteamApps.PICSRequest(app.Value.ID)), 
                                                              callback.PackageChanges.Select(package =>
                                                                  new SteamApps.PICSRequest(package.Value.ID)));
        if (productInfo.Failed || productInfo.Results is null) return;
        foreach (var result in productInfo.Results) {
            foreach (var (_, app) in result.Apps) {
                var pipe = new Pipe();
                app.KeyValues.SaveToStream(pipe.Writer.AsStream(), false);
            }

            foreach (var (_, package) in result.Packages) {
                
            }
        }
#endif
    }

#if PICS_PRODUCT_INFO
    private SortedDictionary<string, string> FlattenKeyValue(KeyValue keyValue) {
        return FlattenKeyValue(keyValue, new SortedDictionary<string, string>());
    }

    private SortedDictionary<string, string> FlattenKeyValue(KeyValue keyValue, SortedDictionary<string, string> current, string parentPath = "") {
        if(keyValue.Value is not null) current.Add($"{parentPath}{keyValue.Name ?? ""}", keyValue.Value);
        
        foreach (var child in keyValue.Children) {
            FlattenKeyValue(child, current, $"{parentPath}{keyValue.Name ?? ""}/");
        }

        return current;
    }
#endif
}

#if STEAM_PACKET_VERBOSE
public sealed partial class VerboseHandler : ClientMsgHandler {
    public override void HandleMsg(IPacketMsg packetMsg) {
        if(packetMsg.MsgType == EMsg.Multi) return;
        Console.WriteLine(packetMsg.MsgType);
    }
}
#endif
