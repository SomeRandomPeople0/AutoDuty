using AutoDuty.Helpers;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.PartyFunctions;

namespace AutoDuty.Managers
{
    internal class MultiboxManager : IDisposable
    {
        private const int DEFAULT_PORT = 7401;
        private const int BUFFER_SIZE = 4096;
        private const int MAX_CLIENTS = 7;

        // Protocol messages
        private const string CLIENT_AUTH = "AD_CLIENT_AUTH";
        private const string SERVER_AUTH = "AD_SERVER_AUTH";
        private const string CLIENT_INFO = "CLIENT_INFO";
        private const string PARTY_INVITE = "PARTY_INVITE";
        private const string KEEPALIVE = "KEEPALIVE";
        private const string KEEPALIVE_RESPONSE = "KEEPALIVE_RESPONSE";
        private const string STEP_START = "STEP_START";
        private const string STEP_COMPLETED = "STEP_COMPLETED";
        private const string DEATH_STATUS = "DEATH_STATUS";
        private const string DEATH_RESET = "DEATH_RESET";
        private const string DUTY_QUEUE = "DUTY_QUEUE";
        private const string DUTY_EXIT = "DUTY_EXIT";

        private TcpServer? _server;
        private TcpClient? _client;
        private bool _isHost;
        private bool _isEnabled;
        private int _port = DEFAULT_PORT;
        private string _hostAddress = "127.0.0.1";

        // Step synchronization
        private bool _stepBlock = false;
        private readonly Dictionary<string, bool> _clientStepStatus = new();
        private readonly Dictionary<string, bool> _clientDeathStatus = new();
        private readonly Dictionary<string, DateTime> _clientKeepAlive = new();
        private readonly Dictionary<string, ClientInfo> _clientInfo = new();

        public record ClientInfo(ulong CID, string Name, ushort WorldId);

        public bool IsEnabled => _isEnabled;
        public bool IsHost => _isHost;
        public bool StepBlocking => _stepBlock && _isEnabled;
        public IReadOnlyDictionary<string, ClientInfo> ConnectedClients => _clientInfo;
        public IReadOnlyDictionary<string, DateTime> ClientKeepAlive => _clientKeepAlive;

        public void Initialize(bool isHost, int port = DEFAULT_PORT, string hostAddress = "127.0.0.1")
        {
            _isHost = isHost;
            _port = port;
            _hostAddress = hostAddress;

            if (_isEnabled)
                Stop();
        }

        public void Start()
        {
            if (_isEnabled) return;

            _isEnabled = true;

            try
            {
                if (_isHost)
                {
                    _server = new TcpServer(_port);
                    _server.ClientConnected += OnClientConnected;
                    _server.ClientDisconnected += OnClientDisconnected;
                    _server.MessageReceived += OnServerMessageReceived;
                    _server.Start();
                    Svc.Log.Info($"Multibox server started on port {_port}");
                }
                else
                {
                    _client = new TcpClient();
                    _client.MessageReceived += OnClientMessageReceived;
                    _client.Disconnected += OnClientDisconnected;
                    Task.Run(() => ConnectToServer());
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"Failed to start multibox: {ex.Message}");
                _isEnabled = false;
            }
        }

        public void Stop()
        {
            if (!_isEnabled) return;

            _isEnabled = false;
            _stepBlock = false;

            _server?.Stop();
            _server = null;

            _client?.Disconnect();
            _client = null;

            _clientStepStatus.Clear();
            _clientDeathStatus.Clear();
            _clientKeepAlive.Clear();
            _clientInfo.Clear();

            Svc.Log.Info("Multibox stopped");
        }

        private async Task ConnectToServer()
        {
            try
            {
                await _client!.ConnectAsync(_hostAddress, _port);
                Svc.Log.Info($"Connected to multibox server at {_hostAddress}:{_port}");

                await _client.SendMessageAsync(CLIENT_AUTH);
                _ = Svc.Framework.RunOnTick(() =>
                {
                    if (Player.CID != 0)
                    {
                        var clientInfo = new
                        {
                            CID = Player.CID,
                            Name = Player.Name,
                            WorldId = Player.CurrentWorldId
                        };
                        _ = _client.SendMessageAsync($"{CLIENT_INFO}|{JsonConvert.SerializeObject(clientInfo)}");
                    }
                });

                _ = Task.Run(ClientKeepAliveLoop);
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"Failed to connect to server: {ex.Message}");
                _isEnabled = false;
            }
        }

        private async Task ClientKeepAliveLoop()
        {
            while (_client?.IsConnected == true)
            {
                try
                {
                    await _client.SendMessageAsync(KEEPALIVE);
                    await Task.Delay(10000);
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning($"Keepalive failed: {ex.Message}");
                    break;
                }
            }
        }

        #region Server Event Handlers

        private void OnClientConnected(string clientId)
        {
            Svc.Log.Info($"Client connected: {clientId}");
            _clientStepStatus[clientId] = false;
            _clientDeathStatus[clientId] = false;
            _clientKeepAlive[clientId] = DateTime.Now;
        }

        private void OnClientDisconnected(string clientId)
        {
            var clientName = _clientInfo.TryGetValue(clientId, out var info) ? info.Name : clientId;
            Svc.Log.Info($"Client disconnected: {clientName} ({clientId})");
            _clientStepStatus.Remove(clientId);
            _clientDeathStatus.Remove(clientId);
            _clientKeepAlive.Remove(clientId);
            _clientInfo.Remove(clientId);
        }

        private async Task OnServerMessageReceived(string clientId, string message)
        {
            var parts = message.Split('|');
            var command = parts[0];

            switch (command)
            {
                case CLIENT_AUTH:
                    await _server!.SendMessageAsync(clientId, SERVER_AUTH);
                    break;

                case CLIENT_INFO:
                    if (parts.Length > 1)
                    {
                        var clientInfo = JsonConvert.DeserializeObject<dynamic>(parts[1]);
                        HandleClientInfo(clientId, clientInfo);
                    }
                    break;

                case KEEPALIVE:
                    _clientKeepAlive[clientId] = DateTime.Now;
                    await _server!.SendMessageAsync(clientId, KEEPALIVE_RESPONSE);
                    break;

                case STEP_COMPLETED:
                    _clientStepStatus[clientId] = true;
                    CheckStepProgress();
                    break;

                case DEATH_STATUS:
                    if (parts.Length > 1 && bool.TryParse(parts[1], out bool isDead))
                    {
                        _clientDeathStatus[clientId] = isDead;
                        CheckDeathStatus();
                    }
                    break;

                default:
                    Svc.Log.Warning($"Unknown message from client {clientId}: {message}");
                    break;
            }
        }

        private void HandleClientInfo(string clientId, dynamic clientInfo)
        {
            try
            {
                ulong cid = clientInfo.CID;
                string name = clientInfo.Name;
                ushort worldId = clientInfo.WorldId;

                // Store client information
                _clientInfo[clientId] = new ClientInfo(cid, name, worldId);

                Svc.Log.Info($"Client identified: {name} ({cid}) from world {worldId}");

                // Invite to party if not already in party
                Svc.Framework.RunOnTick(() =>
                {
                    unsafe
                    {
                        if (!PartyHelper.IsPartyMember(cid))
                        {
                            if (worldId == Player.CurrentWorldId)
                                InfoProxyPartyInvite.Instance()->InviteToParty(cid, name, worldId);
                            else
                                InfoProxyPartyInvite.Instance()->InviteToPartyContentId(cid, 0);

                            _ = _server!.SendMessageAsync(clientId, PARTY_INVITE);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"Failed to handle client info: {ex.Message}");
            }
        }

        #endregion

        #region Client Event Handlers

        private async Task OnClientMessageReceived(string message)
        {
            var parts = message.Split('|');
            var command = parts[0];

            switch (command)
            {
                case SERVER_AUTH:
                    Svc.Log.Info("Authenticated with server");
                    break;

                case STEP_START:
                    if (parts.Length > 1 && int.TryParse(parts[1], out int stepIndex))
                    {
                        _ = Svc.Framework.RunOnTick(() =>
                        {
                            Plugin.Indexer = stepIndex;
                            _stepBlock = false;
                            Svc.Log.Debug($"Step synchronized to index {stepIndex}");
                        });
                    }
                    break;

                case PARTY_INVITE:
                    AcceptPartyInvite();
                    break;

                case DUTY_QUEUE:
                    QueueHelper.InvokeAcceptOnly();
                    break;

                case DUTY_EXIT:
                    ExitDutyHelper.Invoke();
                    break;

                case DEATH_RESET:
                    break;

                case KEEPALIVE_RESPONSE:
                    break;

                default:
                    Svc.Log.Warning($"Unknown message from server: {message}");
                    break;
            }
        }

        private void OnClientDisconnected()
        {
            Svc.Log.Warning("Disconnected from server");
            _isEnabled = false;
        }

        #endregion

        #region Step Synchronization

        public void SetStepBlocking(bool blocking)
        {
            if (!_isEnabled) return;

            if (!blocking && _isHost)
            {
                BroadcastStepStart();
            }

            if (_stepBlock == blocking) return;

            _stepBlock = blocking;

            if (_stepBlock)
            {
                if (_isHost)
                {
                    Plugin.Action = "Waiting for clients";
                    CheckStepProgress();
                }
                else
                {
                    Plugin.Action = "Waiting for others";
                    _ = SendStepCompleted();
                }
            }
        }

        private void BroadcastStepStart()
        {
            if (_server == null) return;

            Svc.Framework.RunOnTick(() =>
            {
                _ = _server.BroadcastMessageAsync($"{STEP_START}|{Plugin.Indexer}");
                Svc.Log.Debug("Broadcasted step start to all clients");
            });
        }

        private async Task SendStepCompleted()
        {
            if (_client?.IsConnected != true) return;

            await _client.SendMessageAsync(STEP_COMPLETED);
            Svc.Log.Debug("Sent step completed to server");
        }

        private void CheckStepProgress()
        {
            if (!_isHost || !_stepBlock) return;

            Svc.Framework.RunOnTick(() =>
            {
                // Check if all clients have completed the step (or if it's a treasure step)
                bool allCompleted = (Plugin.Stage != Stage.Looping &&
                                    Plugin.Indexer >= 0 &&
                                    Plugin.Indexer < Plugin.Actions.Count &&
                                    Plugin.Actions[Plugin.Indexer].Tag == ActionTag.Treasure) ||
                                   _clientStepStatus.Values.All(x => x);

                if (allCompleted)
                {
                    // Reset all client step status
                    var keys = _clientStepStatus.Keys.ToList();
                    foreach (var key in keys)
                        _clientStepStatus[key] = false;

                    _stepBlock = false;
                    Svc.Log.Debug("All clients completed step, proceeding");
                }
            });
        }

        #endregion

        #region Death Synchronization

        public void ReportDeath(bool isDead)
        {
            if (!_isEnabled) return;

            if (_isHost)
            {
                CheckDeathStatus();
            }
            else if (_client?.IsConnected == true)
            {
                _ = _client.SendMessageAsync($"{DEATH_STATUS}|{isDead}");
            }
        }

        private void CheckDeathStatus()
        {
            if (!_isHost) return;

            Svc.Framework.RunOnTick(() =>
            {
                bool allDead = _clientDeathStatus.Values.All(x => x) && Player.IsDead;

                if (allDead)
                {
                    // Reset all death status
                    var keys = _clientDeathStatus.Keys.ToList();
                    foreach (var key in keys)
                        _clientDeathStatus[key] = false;

                    _ = _server?.BroadcastMessageAsync(DEATH_RESET);
                    Svc.Log.Debug("All players dead, sent death reset");
                }
            });
        }

        #endregion

        #region Duty Management

        public void QueueDuty()
        {
            if (!_isHost || _server == null) return;

            _ = _server.BroadcastMessageAsync(DUTY_QUEUE);
            Svc.Log.Debug("Broadcasted duty queue to all clients");
        }

        public void ExitDuty()
        {
            if (!_isHost || _server == null) return;

            _ = _server.BroadcastMessageAsync(DUTY_EXIT);
            Svc.Log.Debug("Broadcasted duty exit to all clients");
        }

        #endregion

        #region Party Management

        private void AcceptPartyInvite()
        {
            SchedulerHelper.ScheduleAction("MultiboxClient PartyInvite Accept", () =>
            {
                unsafe
                {
                    var inviterName = InfoProxyPartyInvite.Instance()->InviterName;

                    if (InfoProxyPartyInvite.Instance()->InviterWorldId != 0 &&
                        UniversalParty.Length <= 1 &&
                        GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) &&
                        GenericHelpers.IsAddonReady(addonSelectYesno))
                    {
                        var yesno = new AddonMaster.SelectYesno(addonSelectYesno);
                        if (yesno.Text.Contains(inviterName.ToString()))
                        {
                            yesno.Yes();
                            SchedulerHelper.DescheduleAction("MultiboxClient PartyInvite Accept");
                        }
                        else
                        {
                            yesno.No();
                        }
                    }
                }
            }, 500, false);
        }

        #endregion

        public void Dispose()
        {
            Stop();
        }
    }

    #region TCP Server/Client Implementation

    internal class TcpServer
    {
        private const int BUFFER_SIZE = 4096;
        private readonly TcpListener _listener;
        private readonly Dictionary<string, System.Net.Sockets.TcpClient> _clients = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _isRunning;

        public event Action<string>? ClientConnected;
        public event Action<string>? ClientDisconnected;
        public event Func<string, string, Task>? MessageReceived;

        public TcpServer(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }

        public void Start()
        {
            _listener.Start();
            _isRunning = true;
            _ = Task.Run(AcceptClientsAsync);
        }

        public void Stop()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();

            foreach (var client in _clients.Values)
                client.Close();

            _clients.Clear();
            _listener.Stop();
        }

        private async Task AcceptClientsAsync()
        {
            while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    var clientId = Guid.NewGuid().ToString();
                    _clients[clientId] = tcpClient;

                    ClientConnected?.Invoke(clientId);
                    _ = Task.Run(() => HandleClientAsync(clientId, tcpClient));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Svc.Log.Error($"Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(string clientId, System.Net.Sockets.TcpClient tcpClient)
        {
            var buffer = new byte[BUFFER_SIZE];
            var stream = tcpClient.GetStream();

            try
            {
                while (tcpClient.Connected && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (MessageReceived != null)
                        await MessageReceived.Invoke(clientId, message);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Client {clientId} error: {ex.Message}");
            }
            finally
            {
                _clients.Remove(clientId);
                tcpClient.Close();
                ClientDisconnected?.Invoke(clientId);
            }
        }

        public async Task SendMessageAsync(string clientId, string message)
        {
            if (_clients.TryGetValue(clientId, out var client) && client.Connected)
            {
                var data = Encoding.UTF8.GetBytes(message);
                await client.GetStream().WriteAsync(data, 0, data.Length);
            }
        }

        public async Task BroadcastMessageAsync(string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            var tasks = new List<Task>();

            foreach (var client in _clients.Values.Where(c => c.Connected))
            {
                tasks.Add(client.GetStream().WriteAsync(data, 0, data.Length));
            }

            await Task.WhenAll(tasks);
        }
    }

    internal class TcpClient
    {
        private const int BUFFER_SIZE = 4096;
        private System.Net.Sockets.TcpClient? _client;
        private NetworkStream? _stream;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public event Func<string, Task>? MessageReceived;
        public event Action? Disconnected;

        public bool IsConnected => _client?.Connected == true;

        public async Task ConnectAsync(string host, int port)
        {
            _client = new System.Net.Sockets.TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
            _ = Task.Run(ReceiveMessagesAsync);
        }

        public void Disconnect()
        {
            _cancellationTokenSource.Cancel();
            _stream?.Close();
            _client?.Close();
            Disconnected?.Invoke();
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[BUFFER_SIZE];

            try
            {
                while (_client?.Connected == true && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (MessageReceived != null)
                        await MessageReceived.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Receive error: {ex.Message}");
            }
            finally
            {
                Disconnected?.Invoke();
            }
        }

        public async Task SendMessageAsync(string message)
        {
            if (_stream != null && _client?.Connected == true)
            {
                var data = Encoding.UTF8.GetBytes(message);
                await _stream.WriteAsync(data, 0, data.Length);
            }
        }
    }

    #endregion
}