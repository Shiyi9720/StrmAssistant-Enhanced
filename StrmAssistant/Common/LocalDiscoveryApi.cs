using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.ApiClient;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Lets the admin replace the address Emby advertises to LAN clients. Clients either ask for
    /// "who is EmbyServer?" on UDP port 7359 or read the public system info endpoint, and in both
    /// cases they normally receive the server's LAN IP with the local HTTP port. When a custom
    /// address (usually a local reverse proxy) is configured, that address is returned instead.
    /// The special keyword BLOCKED disables the UDP reply.
    ///
    /// Emby's own UDP responder is replaced by binding the port before it starts, which is why the
    /// entry point runs as IRunBeforeStartup. Everything is best effort: an unset or invalid
    /// option leaves the built-in behavior untouched and any failure is logged and ignored.
    /// </summary>
    public static class LocalDiscoveryApi
    {
        private const int UdpPort = 7359;
        private const string BlockedKeyword = "BLOCKED";

        private static readonly string[] AcceptedText =
        {
            "who is EmbyServer?",
            "who is MediaBrowserServer_v2?"
        };

        private static readonly Encoding[] AcceptedEncodings =
        {
            Encoding.UTF8,
            Encoding.Unicode
        };

        private static readonly object SyncLock = new object();
        private static Socket _udpSocket;
        private static CancellationTokenSource _cancellation;

        public static string ConfiguredValue
        {
            get
            {
                try
                {
                    return (Plugin.Instance?.GetPluginOptions()?.NetworkOptions?.LocalDiscoveryAddress ??
                            string.Empty).Trim();
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Returns the normalized address to advertise, or null when the option is unset, set to
        /// BLOCKED, or invalid (in which case the caller keeps Emby's default behavior).
        /// </summary>
        public static string GetEffectiveAddress()
        {
            return TryNormalizeAddress(ConfiguredValue);
        }

        private static string TryNormalizeAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, BlockedKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var address = value.Trim().TrimEnd('/');
            if (address.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                address = "http://" + address;
            }

            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrEmpty(uri.Host))
            {
                return address;
            }

            return null;
        }

        internal static void Initialize(IServerApplicationHost applicationHost, IJsonSerializer jsonSerializer,
            ILogger logger)
        {
            try
            {
                var raw = ConfiguredValue;
                if (string.IsNullOrEmpty(raw))
                {
                    logger?.Debug("LocalDiscoveryAddress is EMPTY. Built-in server discovery is used.");
                    return;
                }

                var blocked = string.Equals(raw, BlockedKeyword, StringComparison.OrdinalIgnoreCase);
                var address = TryNormalizeAddress(raw);

                if (!blocked && address is null)
                {
                    logger?.Warn(
                        "LocalDiscoveryAddress {0} is not a valid http/https URL. Built-in server discovery is used.",
                        raw);
                    return;
                }

                if (blocked)
                {
                    logger?.Info("LocalDiscoveryAddress is BLOCKED. UDP discovery responses are disabled.");
                }
                else
                {
                    logger?.Info("LocalDiscoveryAddress is set to {0}", address);
                }

                StartUdpServer(applicationHost, jsonSerializer, blocked, address, logger);

                if (!blocked)
                {
                    TryOverridePublicSystemInfoRoute(applicationHost, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("LocalDiscoveryAddress setup failed: {0}. Built-in server discovery is used.",
                    ex.Message);
            }
        }

        public static void Dispose()
        {
            lock (SyncLock)
            {
                StopUdpServer();
            }
        }

        private static void StartUdpServer(IServerApplicationHost applicationHost, IJsonSerializer jsonSerializer,
            bool blocked, string address, ILogger logger)
        {
            lock (SyncLock)
            {
                StopUdpServer();

                Socket socket;
                try
                {
                    socket = CreateUdpSocket();
                }
                catch (Exception ex)
                {
                    logger?.Warn("Unable to bind UDP port {0} for local discovery: {1}. Emby's built-in discovery remains in use.",
                        UdpPort, ex.Message);
                    return;
                }

                _udpSocket = socket;
                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;

                Task.Factory.StartNew(() => Listen(socket, applicationHost, jsonSerializer, blocked, address, logger, token),
                    token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        private static void StopUdpServer()
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch
            {
                // Ignore: the listener is already shutting down.
            }

            try
            {
                _udpSocket?.Dispose();
            }
            catch
            {
                // Ignore: the socket is already closed.
            }

            _udpSocket = null;
            _cancellation = null;
        }

        /// <summary>
        /// Binds UDP 7359 before Emby's own responder does. SO_REUSEADDR is deliberately not set:
        /// on Unix the later bind of Emby's responder then fails, so only this responder answers.
        /// On Windows the exclusive address option provides the same effect.
        /// </summary>
        private static Socket CreateUdpSocket()
        {
            try
            {
                var dualMode = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    dualMode.DualMode = true;
                    ConfigureExclusiveAddressUse(dualMode);
                    dualMode.Bind(new IPEndPoint(IPAddress.IPv6Any, UdpPort));
                    return dualMode;
                }
                catch
                {
                    dualMode.Dispose();
                }
            }
            catch
            {
                // Dual mode sockets are not available on every platform; fall back to IPv4.
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                ConfigureExclusiveAddressUse(socket);
                socket.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static void ConfigureExclusiveAddressUse(Socket socket)
        {
            try
            {
                socket.ExclusiveAddressUse = true;
            }
            catch
            {
                // Only supported on Windows; Unix uses the SO_REUSEADDR rules described above.
            }
        }

        /// <summary>
        /// Blocking receive loop on a dedicated thread. Discovery traffic is tiny, and closing the
        /// socket is enough to unblock the receive when the plugin is disposed.
        /// </summary>
        private static void Listen(Socket socket, IServerApplicationHost applicationHost,
            IJsonSerializer jsonSerializer, bool blocked, string address, ILogger logger, CancellationToken token)
        {
            var buffer = new byte[8192];

            while (!token.IsCancellationRequested)
            {
                EndPoint remoteEndPoint = socket.AddressFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0)
                    : new IPEndPoint(IPAddress.Any, 0);

                int received;
                try
                {
                    received = socket.ReceiveFrom(buffer, ref remoteEndPoint);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    // Avoid a busy loop on transient socket errors (for example ICMP unreachable).
                    Thread.Sleep(50);
                    continue;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    logger?.Debug("Local discovery receive failed: {0}", ex.Message);
                    Thread.Sleep(50);
                    continue;
                }

                if (blocked || received <= 0)
                {
                    continue;
                }

                try
                {
                    foreach (var encoding in AcceptedEncodings)
                    {
                        var text = encoding.GetString(buffer, 0, received);
                        if (!IsDiscoveryRequest(text))
                        {
                            continue;
                        }

                        var payload = jsonSerializer.SerializeToString(new ServerDiscoveryInfo
                        {
                            Address = address,
                            Id = applicationHost.SystemId,
                            Name = applicationHost.FriendlyName
                        });
                        socket.SendTo(encoding.GetBytes(payload), remoteEndPoint);
                        logger?.Debug("Local discovery response sent to {0}", remoteEndPoint);
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger?.Debug("Local discovery response failed: {0}", ex.Message);
                }
            }
        }

        private static bool IsDiscoveryRequest(string text)
        {
            foreach (var accepted in AcceptedText)
            {
                if (text != null && text.IndexOf(accepted, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Emby serves /System/Info/Public from its own service and fills LocalAddresses from the
        /// detected LAN IP. A plugin service with the same route is appended to the route table,
        /// so move it to the front of its bucket: matching picks the first route on a tie.
        /// </summary>
        private static void TryOverridePublicSystemInfoRoute(IServerApplicationHost applicationHost, ILogger logger)
        {
            try
            {
                var httpServer = applicationHost.Resolve<IHttpServer>();
                var controller = httpServer?.GetType()
                    .GetProperty("ServiceController", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(httpServer);
                if (controller is null)
                {
                    logger?.Warn(
                        "LocalDiscoveryAddress: HTTP service controller not found. The public system info endpoint keeps the default address.");
                    return;
                }

                var map = controller.GetType()
                    .GetField("RestPathMap", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(controller) as IDictionary;
                if (map is null)
                {
                    logger?.Warn(
                        "LocalDiscoveryAddress: HTTP route table not found. The public system info endpoint keeps the default address.");
                    return;
                }

                if (!PrioritizeRestPath(map, typeof(Web.Api.GetLocalDiscoveryPublicSystemInfo)))
                {
                    // The plugin service was not registered by Emby's scan; register it now.
                    var init = controller.GetType().GetMethod("Init", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { httpServer.GetType(), typeof(Type[]) }, null);
                    init?.Invoke(controller,
                        new object[] { httpServer, new[] { typeof(Web.Service.LocalDiscoverySystemService) } });

                    if (!PrioritizeRestPath(map, typeof(Web.Api.GetLocalDiscoveryPublicSystemInfo)))
                    {
                        logger?.Warn(
                            "LocalDiscoveryAddress: unable to override the public system info address. That endpoint keeps the default address.");
                        return;
                    }
                }

                logger?.Info("The public system info endpoint now advertises the local discovery address.");
            }
            catch (Exception ex)
            {
                logger?.Warn(
                    "LocalDiscoveryAddress: unable to override the public system info address ({0}). That endpoint keeps the default address.",
                    ex.Message);
            }
        }

        private static bool PrioritizeRestPath(IDictionary map, Type requestType)
        {
            foreach (DictionaryEntry entry in map)
            {
                if (!(entry.Value is IList list) || list.Count == 0)
                {
                    continue;
                }

                var index = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    var requestTypeProperty = list[i]?.GetType()
                        .GetProperty("RequestType", BindingFlags.Public | BindingFlags.Instance);
                    if (requestTypeProperty?.GetValue(list[i]) as Type == requestType)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    continue;
                }

                if (index > 0)
                {
                    var restPath = list[index];
                    list.RemoveAt(index);
                    list.Insert(0, restPath);
                }

                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Starts the custom local discovery before Emby's UDP entry point runs, so the plugin gets
    /// the port first. Runs in the "before startup" pass, which Emby completes before it starts
    /// the built-in discovery responder.
    /// </summary>
    public sealed class LocalDiscoveryEntryPoint : IServerEntryPoint, IRunBeforeStartup
    {
        private readonly IServerApplicationHost _applicationHost;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public LocalDiscoveryEntryPoint(IServerApplicationHost applicationHost, IJsonSerializer jsonSerializer,
            ILogManager logManager)
        {
            _applicationHost = applicationHost;
            _jsonSerializer = jsonSerializer;
            _logger = logManager?.GetLogger("LocalDiscovery");
        }

        public void Run()
        {
            try
            {
                LocalDiscoveryApi.Initialize(_applicationHost, _jsonSerializer, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Warn("Local discovery initialization failed: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            LocalDiscoveryApi.Dispose();
        }
    }
}