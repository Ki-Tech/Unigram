﻿using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Telegram.Api.Aggregator;
using Telegram.Api.Helpers;
using Telegram.Api.Services;
using Telegram.Api.Services.Cache;
using Telegram.Api.Services.Connection;
using Telegram.Api.Services.Updates;
using Telegram.Api.TL;
using Telegram.Api.TL.Methods.Messages;
using Telegram.Api.TL.Methods.Phone;
using Telegram.Api.Transport;
using Unigram.Core;
using Unigram.Core.Services;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Calls;
using Windows.Foundation;
using Windows.Foundation.Collections;
using libtgvoip;
using Windows.Storage;
using System.Net;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;

namespace Unigram.Tasks
{
    public sealed class VoIPCallTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;

        internal static IBackgroundTaskInstance _instance;
        public static VoIPCallTask Current { get; private set; }
        internal static VoIPCallMediator _mediator;
        internal static VoIPCallMediator Mediator
        {
            get
            {
                if (_mediator == null)
                {
                    VoIPCallTask.Log("Mediator doesn't exists", "Creating mediator");
                    _mediator = new VoIPCallMediator();
                }

                return _mediator;
            }
        }

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            _instance = taskInstance;

            VoIPCallTask.Log("VoIPCallTask started", GetCurrentProcessId().ToString());

            Current = this;
            Mediator.Initialize(_deferral);
            taskInstance.Canceled += OnCanceled;
        }

        public void OutgoingCall(int userId, long accessHash)
        {
            Mediator.OutgoingCall(userId, accessHash);
        }

        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            _mediator?.Dispose();
            _mediator = null;

            VoIPCallTask.Log("Releasing background task", "Releasing background task");

            _deferral.Complete();
        }

        [Conditional("DEBUG")]
        internal static void Log(string caption, string message)
        {
            var xml = $@"
                <toast>
                    <visual>
                      <binding template='ToastGeneric'>
                        <text>{WebUtility.HtmlEncode(caption) ?? string.Empty}</text>
                        <text>{WebUtility.HtmlEncode(message) ?? string.Empty}</text>
                        <text placement='attribution'>Unigram</text>
                      </binding>
                    </visual>
               </toast>";

            var notifier = ToastNotificationManager.CreateToastNotifier("App");
            var document = new XmlDocument();
            document.LoadXml(xml);

            try
            {
                var notification = new ToastNotification(document);
                notification.Group = "voip";
                notification.SuppressPopup = true;

                notifier.Show(notification);
            }
            catch { }
        }

    }

    internal class VoIPCallMediator : IHandle<TLUpdatePhoneCall>, IHandle, IDisposable, IStateCallback
    {
        private readonly Queue<TLUpdatePhoneCall> _queue = new Queue<TLUpdatePhoneCall>();

        private AppServiceConnection _connection;
        private MTProtoService _protoService;
        private TransportService _transportService;

        private VoIPControllerWrapper _controller;

        private VoipPhoneCall _systemCall;
        private TLPhoneCallBase _phoneCall;
        private TLPhoneCallState _state;
        private TLUserBase _user;
        private bool _outgoing;

        private BackgroundTaskDeferral _deferral;
        private bool _initialized;

        public VoIPCallMediator()
        {
            VoIPCallTask.Log("Mediator constructed", "Mediator constructed");
        }

        public async void Initialize(AppServiceConnection connection)
        {
            if (connection != null)
            {
                VoIPCallTask.Log("Mediator initialized", "Connecting app service");

                _connection = connection;
                _connection.RequestReceived += OnRequestReceived;
            }
            else
            {
                VoIPCallTask.Log("Mediator initialized", "Disconnetting app service");

                if (_connection != null)
                {
                    _connection.RequestReceived -= OnRequestReceived;
                    _connection = null;
                }
            }

            if (_protoService != null && _connection != null)
            {
                VoIPCallTask.Log("Mediator initialized", "Disposing proto service");

                _protoService.Dispose();
                _transportService.Close();
            }

            if (_phoneCall != null && _connection != null)
            {
                await UpdateCallAsync(string.Empty);
            }
        }

        public void Initialize(BackgroundTaskDeferral deferral)
        {
            if (_connection == null & _protoService == null)
            {
                VoIPCallTask.Log("Mediator initialized", "Creating proto service");

                var deviceInfoService = new DeviceInfoService();
                var eventAggregator = new TelegramEventAggregator();
                var cacheService = new InMemoryCacheService(eventAggregator);
                var updatesService = new UpdatesService(cacheService, eventAggregator);
                var transportService = new TransportService();
                var connectionService = new ConnectionService(deviceInfoService);
                var statsService = new StatsService();
                var protoService = new MTProtoService(deviceInfoService, updatesService, cacheService, transportService, connectionService, statsService);

                protoService.Initialized += (s, args) =>
                {
                    VoIPCallTask.Log("ProtoService initialized", "waiting for updates");

                    updatesService.LoadStateAndUpdate(() =>
                    {
                        VoIPCallTask.Log("Difference processed", "Difference processed");

                        if (_phoneCall == null)
                        {
                            VoIPCallTask.Log("Difference processed", "No call found in difference");

                            if (_systemCall != null)
                            {
                                _systemCall.NotifyCallEnded();
                            }
                        }
                    });
                };

                eventAggregator.Subscribe(this);
                protoService.Initialize();
                _protoService = protoService;
                _transportService = transportService;
            }
            else
            {
                VoIPCallTask.Log("Mediator initialized", "_connection is null: " + (_connection == null));
            }

            _deferral = deferral;
            _initialized = true;

            ProcessUpdates();
        }

        private async void ProcessUpdates()
        {
            while (_queue.Count > 0)
            {
                var update = _queue.Dequeue();
                if (update.PhoneCall is TLPhoneCallRequested requested)
                {
                    _phoneCall = requested;

                    var req = new TLPhoneReceivedCall { Peer = new TLInputPhoneCall { Id = requested.Id, AccessHash = requested.AccessHash } };

                    const string caption = "phone.receivedCall";
                    var response = await SendRequestAsync<bool>(caption, req);

                    var responseUser = await SendRequestAsync<TLUser>("voip.getUser", new TLPeerUser { UserId = requested.AdminId });
                    if (responseUser.Result == null)
                    {
                        return;
                    }

                    var user = responseUser.Result;
                    var photo = new Uri("ms-appx:///Assets/Logos/Square150x150Logo/Square150x150Logo.png");
                    if (user.Photo is TLUserProfilePhoto profile && profile.PhotoSmall is TLFileLocation location)
                    {
                        var fileName = string.Format("{0}_{1}_{2}.jpg", location.VolumeId, location.LocalId, location.Secret);
                        var temp = FileUtils.GetTempFileUri(fileName);

                        photo = temp;
                    }

                    var coordinator = VoipCallCoordinator.GetDefault();
                    var call = coordinator.RequestNewIncomingCall("Unigram", user.FullName, user.DisplayName, photo, "Unigram", null, "Unigram", null, VoipPhoneCallMedia.Audio, TimeSpan.FromSeconds(128));

                    _user = user;
                    _outgoing = false;
                    _systemCall = call;
                    _systemCall.AnswerRequested += OnAnswerRequested;
                    _systemCall.RejectRequested += OnRejectRequested;
                }
                else if (update.PhoneCall is TLPhoneCallDiscarded discarded)
                {
                    _phoneCall = discarded;

                    if (_controller != null)
                    {
                        _controller.Dispose();
                        _controller = null;
                    }

                    if (_systemCall != null)
                    {
                        _systemCall.AnswerRequested -= OnAnswerRequested;
                        _systemCall.RejectRequested -= OnRejectRequested;
                        _systemCall.NotifyCallEnded();
                        _systemCall = null;
                    }
                    else if (_deferral != null)
                    {
                        _deferral.Complete();
                    }
                }
                else if (update.PhoneCall is TLPhoneCallAccepted accepted)
                {
                    await UpdateStateAsync(TLPhoneCallState.ExchangingKeys);

                    _phoneCall = accepted;

                    auth_key = computeAuthKey(accepted);

                    byte[] authKeyHash = Utils.ComputeSHA1(auth_key);
                    byte[] authKeyId = new byte[8];
                    Buffer.BlockCopy(authKeyHash, authKeyHash.Length - 8, authKeyId, 0, 8);
                    long fingerprint = Utils.BytesToLong(authKeyId);
                    //this.authKey = authKey;
                    //keyFingerprint = fingerprint;

                    var request = new TLPhoneConfirmCall
                    {
                        GA = g_a,
                        KeyFingerprint = fingerprint,
                        Peer = new TLInputPhoneCall
                        {
                            Id = accepted.Id,
                            AccessHash = accepted.AccessHash
                        },
                        Protocol = new TLPhoneCallProtocol
                        {
                            IsUdpP2p = true,
                            IsUdpReflector = true,
                            MinLayer = 65,
                            MaxLayer = 65,
                        }
                    };

                    var response = await SendRequestAsync<TLPhonePhoneCall>("phone.confirmCall", request);
                    if (response.IsSucceeded)
                    {
                        _systemCall.NotifyCallActive();

                        Handle(new TLUpdatePhoneCall { PhoneCall = response.Result.PhoneCall });
                    }
                }
                else if (update.PhoneCall is TLPhoneCall call)
                {
                    _phoneCall = call;

                    if (auth_key == null)
                    {
                        auth_key = computeAuthKey(call);
                        g_a = call.GAOrB;
                    }

                    var buffer = TLUtils.Combine(auth_key, g_a);
                    var sha256 = Utils.ComputeSHA256(buffer);

                    var emoji = EncryptionKeyEmojifier.EmojifyForCall(sha256);

                    await UpdateCallAsync(string.Join(" ", emoji));

                    var response = await SendRequestAsync<TLDataJSON>("phone.getCallConfig", new TLPhoneGetCallConfig());
                    if (response.IsSucceeded)
                    {
                        var responseConfig = await SendRequestAsync<TLConfig>("voip.getConfig", new TLPeerUser());
                        var config = responseConfig.Result;

                        VoIPControllerWrapper.UpdateServerConfig(response.Result.Data);

                        var logFile = ApplicationData.Current.LocalFolder.Path + "\\tgvoip.logFile.txt";
                        var statsDumpFile = ApplicationData.Current.LocalFolder.Path + "\\tgvoip.statsDump.txt";

                        if (_controller != null)
                        {
                            _controller.Dispose();
                            _controller = null;
                        }

                        _controller = new VoIPControllerWrapper();
                        _controller.SetConfig(config.CallPacketTimeoutMs / 1000.0, config.CallConnectTimeoutMs / 1000.0, DataSavingMode.Never, false, false, true, logFile, statsDumpFile);

                        _controller.SetStateCallback(this);
                        _controller.SetEncryptionKey(auth_key, _outgoing);

                        var connection = call.Connection;
                        var endpoints = new Endpoint[call.AlternativeConnections.Count + 1];
                        endpoints[0] = connection.ToEndpoint();

                        for (int i = 0; i < call.AlternativeConnections.Count; i++)
                        {
                            connection = call.AlternativeConnections[i];
                            endpoints[i + 1] = connection.ToEndpoint();
                        }

                        _controller.SetPublicEndpoints(endpoints, call.Protocol.IsUdpP2p);
                        _controller.Start();
                        _controller.Connect();
                    }

                    //await Task.Delay(50000);

                    //var req = new TLPhoneDiscardCall { Peer = new TLInputPhoneCall { Id = call.Id, AccessHash = call.AccessHash }, Reason = new TLPhoneCallDiscardReasonHangup() };

                    //const string caption = "phone.discardCall";
                    //await SendRequestAsync<TLUpdatesBase>(caption, req);

                    //_systemCall.NotifyCallEnded();
                }
                else if (update.PhoneCall is TLPhoneCallWaiting waiting)
                {
                    if (_state == TLPhoneCallState.Waiting && waiting.HasReceiveDate && waiting.ReceiveDate != 0)
                    {
                        await UpdateStateAsync(TLPhoneCallState.Ringing);
                    }
                }
            }
        }

        public async void OnCallStateChanged(CallState newState)
        {
            if (newState == CallState.Failed)
            {
                var error = _controller.GetLastError();
            }

            await UpdateStateAsync((TLPhoneCallState)newState);
        }

        private async Task UpdateCallAsync(string emoji)
        {
            if (_connection != null)
            {
                VoIPCallTask.Log("Mediator initialized", "Informing foreground about current call");

                var data = TLTuple.Create((int)_state, _phoneCall, _user, emoji);
                await _connection.SendMessageAsync(new ValueSet { { "caption", "voip.callInfo" }, { "request", TLSerializationService.Current.Serialize(data) } });
            }
        }

        private Task UpdateStateAsync(TLPhoneCallState state)
        {
            if (_state != state)
            {
                Debug.WriteLine("State changed in task: " + state);

                _state = state;
                return UpdateCallAsync(string.Empty);
            }

            return Task.CompletedTask;
        }

        private byte[] auth_key;
        private byte[] secretP;
        private byte[] a_or_b;
        private byte[] g_a;

        private async void OnAnswerRequested(VoipPhoneCall sender, CallAnswerEventArgs args)
        {
            if (_phoneCall != null)
            {
                await UpdateStateAsync(TLPhoneCallState.ExchangingKeys);

                var reqConfig = new TLMessagesGetDHConfig { Version = 0, RandomLength = 256 };

                var config = await SendRequestAsync<TLMessagesDHConfig>("messages.getDhConfig", reqConfig);
                if (config.IsSucceeded)
                {
                    var dh = config.Result;
                    if (!TLUtils.CheckPrime(dh.P, dh.G))
                    {
                        return;
                    }

                    var salt = new byte[256];
                    var secureRandom = new SecureRandom();
                    secureRandom.NextBytes(salt);

                    secretP = dh.P;
                    a_or_b = salt;

                    var g_b = MTProtoService.GetGB(salt, dh.G, dh.P);

                    var request = new TLPhoneAcceptCall
                    {
                        GB = g_b,
                        Peer = _phoneCall.ToInputPhoneCall(),
                        Protocol = new TLPhoneCallProtocol
                        {
                            IsUdpP2p = true,
                            IsUdpReflector = true,
                            MinLayer = 65,
                            MaxLayer = 65,
                        }
                    };

                    var response = await SendRequestAsync<TLPhonePhoneCall>("phone.acceptCall", request);
                    if (response.IsSucceeded)
                    {
                    }
                }

                _systemCall.NotifyCallActive();
            }
        }

        private byte[] computeAuthKey(TLPhoneCall call)
        {
            BigInteger g_a = new BigInteger(1, call.GAOrB);
            BigInteger p = new BigInteger(1, secretP);

            g_a = g_a.ModPow(new BigInteger(1, a_or_b), p);

            byte[] authKey = g_a.ToByteArray();
            if (authKey.Length > 256)
            {
                byte[] correctedAuth = new byte[256];
                Buffer.BlockCopy(authKey, authKey.Length - 256, correctedAuth, 0, 256);
                authKey = correctedAuth;
            }
            else if (authKey.Length < 256)
            {
                byte[] correctedAuth = new byte[256];
                Buffer.BlockCopy(authKey, 0, correctedAuth, 256 - authKey.Length, authKey.Length);
                for (int a = 0; a < 256 - authKey.Length; a++)
                {
                    authKey[a] = 0;
                }
                authKey = correctedAuth;
            }
            byte[] authKeyHash = Utils.ComputeSHA1(authKey);
            byte[] authKeyId = new byte[8];
            Buffer.BlockCopy(authKeyHash, authKeyHash.Length - 8, authKeyId, 0, 8);

            return authKey;
        }

        private byte[] computeAuthKey(TLPhoneCallAccepted call)
        {
            BigInteger p = new BigInteger(1, secretP);
            BigInteger i_authKey = new BigInteger(1, call.GB);

            i_authKey = i_authKey.ModPow(new BigInteger(1, a_or_b), p);

            byte[] authKey = i_authKey.ToByteArray();
            if (authKey.Length > 256)
            {
                byte[] correctedAuth = new byte[256];
                Buffer.BlockCopy(authKey, authKey.Length - 256, correctedAuth, 0, 256);
                authKey = correctedAuth;
            }
            else if (authKey.Length < 256)
            {
                byte[] correctedAuth = new byte[256];
                Buffer.BlockCopy(authKey, 0, correctedAuth, 256 - authKey.Length, authKey.Length);
                for (int a = 0; a < 256 - authKey.Length; a++)
                {
                    authKey[a] = 0;
                }
                authKey = correctedAuth;
            }

            return authKey;
        }

        private async void OnRejectRequested(VoipPhoneCall sender, CallRejectEventArgs args)
        {
            if (_phoneCall is TLPhoneCallRequested requested)
            {
                var req = new TLPhoneDiscardCall { Peer = new TLInputPhoneCall { Id = requested.Id, AccessHash = requested.AccessHash }, Reason = new TLPhoneCallDiscardReasonBusy() };

                const string caption = "phone.discardCall";
                await SendRequestAsync<TLUpdatesBase>(caption, req);

                _systemCall.NotifyCallEnded();
            }
        }

        private async Task<MTProtoResponse<T>> SendRequestAsync<T>(string caption, TLObject request)
        {
            if (_protoService != null)
            {
                if (caption.Equals("voip.getUser"))
                {
                    return new MTProtoResponse<T>(InMemoryCacheService.Current.GetUser(((TLPeerUser)request).UserId));
                }

                return await _protoService.SendRequestAsync<T>(caption, request);
            }
            else
            {

                var response = await _connection.SendMessageAsync(new ValueSet { { nameof(caption), caption }, { nameof(request), TLSerializationService.Current.Serialize(request) } });
                if (response.Status == AppServiceResponseStatus.Success)
                {
                    if (response.Message.ContainsKey("result"))
                    {
                        return new MTProtoResponse<T>(TLSerializationService.Current.Deserialize(response.Message["result"] as string));
                    }
                    else if (response.Message.ContainsKey("error"))
                    {
                        return new MTProtoResponse<T>(TLSerializationService.Current.Deserialize<TLRPCError>(response.Message["error"] as string));
                    }
                }

                return new MTProtoResponse<T>(new TLRPCError { ErrorMessage = "UNKNOWN", ErrorCode = (int)response.Status });
            }
        }

        private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var deferral = args.GetDeferral();
            var message = args.Request.Message;

            if (message.ContainsKey("update"))
            {
                var buffer = message["update"] as string;
                var update = TLSerializationService.Current.Deserialize(buffer) as TLUpdatePhoneCall;
                if (update != null)
                {
                    Debug.WriteLine(update.PhoneCall);
                    _queue.Enqueue(update);

                    if (_initialized)
                    {
                        ProcessUpdates();
                        await UpdateCallAsync(string.Empty);
                    }
                }
            }
            else if (message.ContainsKey("caption"))
            {
                var caption = message["caption"] as string;
                if (caption.Equals("phone.discardCall"))
                {
                    if (_phoneCall != null)
                    {
                        var req = new TLPhoneDiscardCall { Peer = _phoneCall.ToInputPhoneCall(), Reason = new TLPhoneCallDiscardReasonHangup() };

                        const string caption2 = "phone.discardCall";
                        var response = await SendRequestAsync<TLUpdatesBase>(caption2, req);
                        if (response.IsSucceeded)
                        {
                            if (response.Result is TLUpdates updates)
                            {
                                var update = updates.Updates.FirstOrDefault(x => x is TLUpdatePhoneCall) as TLUpdatePhoneCall;
                                if (update != null)
                                {
                                    Handle(update);
                                }
                            }
                        }
                    }
                }
                else if (caption.Equals("phone.mute") || caption.Equals("phone.unmute"))
                {
                    if (_controller != null)
                    {
                        _controller.SetMicMute(caption.Equals("phone.mute"));
                    }
                }
                else if (caption.Equals("voip.startCall"))
                {
                    var buffer = message["request"] as string;
                    var req = TLSerializationService.Current.Deserialize<TLUser>(buffer);

                    _user = req;
                    OutgoingCall(req.Id, req.AccessHash.Value);
                }
            }
            else if (message.ContainsKey("voip.callInfo"))
            {
                if (_phoneCall != null)
                {
                    await args.Request.SendResponseAsync(new ValueSet { { "result", TLSerializationService.Current.Serialize(_phoneCall) } });
                }
                else
                {
                    await args.Request.SendResponseAsync(new ValueSet { { "error", false } });
                }
            }

            deferral.Complete();
        }

        public async void Handle(TLUpdatePhoneCall update)
        {
            Debug.WriteLine(update.PhoneCall);
            _queue.Enqueue(update);

            if (_initialized)
            {
                ProcessUpdates();
                await UpdateCallAsync(string.Empty);
            }
        }

        public void Dispose()
        {
            VoIPCallTask.Log("Releasing background task", "Disposing mediator");

            if (_protoService != null)
            {
                _protoService.Dispose();
                _transportService.Close();
            }
        }

        internal async void OutgoingCall(int userId, long accessHash)
        {
            await UpdateStateAsync(TLPhoneCallState.Requesting);

            var coordinator = VoipCallCoordinator.GetDefault();
            var call = coordinator.RequestNewOutgoingCall("Unigram", _user.FullName, "Unigram", VoipPhoneCallMedia.Audio);

            _outgoing = true;
            _systemCall = call;
            _systemCall.AnswerRequested += OnAnswerRequested;
            _systemCall.RejectRequested += OnRejectRequested;

            var reqConfig = new TLMessagesGetDHConfig { Version = 0, RandomLength = 256 };

            var config = await SendRequestAsync<TLMessagesDHConfig>("messages.getDhConfig", reqConfig);
            if (config.IsSucceeded)
            {
                var dh = config.Result;
                if (!TLUtils.CheckPrime(dh.P, dh.G))
                {
                    return;
                }

                var salt = new byte[256];
                var secureRandom = new SecureRandom();
                secureRandom.NextBytes(salt);

                secretP = dh.P;
                a_or_b = salt;
                g_a = MTProtoService.GetGB(salt, dh.G, dh.P);

                var request = new TLPhoneRequestCall
                {
                    UserId = new TLInputUser { UserId = userId, AccessHash = accessHash },
                    RandomId = TLInt.Random(),
                    GAHash = Utils.ComputeSHA256(g_a),
                    Protocol = new TLPhoneCallProtocol
                    {
                        IsUdpP2p = true,
                        IsUdpReflector = true,
                        MinLayer = Telegram.Api.Constants.CallsMinLayer,
                        MaxLayer = Telegram.Api.Constants.CallsMaxLayer,
                    }
                };

                var response = await SendRequestAsync<TLPhonePhoneCall>("phone.requestCall", request);
                if (response.IsSucceeded)
                {
                    var update = new TLUpdatePhoneCall { PhoneCall = response.Result.PhoneCall };

                    _state = TLPhoneCallState.Waiting;
                    Handle(update);
                }
                else
                {
                    Debugger.Break();
                }
            }
        }
    }
}
