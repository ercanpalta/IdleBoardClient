using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace IdleBoardClient
{
    public record DiscoveredServer(string ServerUrl, string EventsUrl, string PrimaryIp, int Port, string Hostname);

    public static class ServerDiscoveryService
    {
        public const int DiscoveryPort = 38899;
        private const string PingMessage = "IDLEBOARD_DISCOVER";

        /// <summary>
        /// Yerel ağdaki (LAN) IdleBoard sunucusunu UDP Broadcast ile otomatik tespit eder.
        /// </summary>
        public static async Task<DiscoveredServer?> DiscoverServerAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            try
            {
                using var udpClient = new UdpClient(AddressFamily.InterNetwork);
                udpClient.EnableBroadcast = true;
                udpClient.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;

                var requestBytes = Encoding.UTF8.GetBytes(PingMessage);
                var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

                // 255.255.255.255 adresine keşif paketi fırlat
                await udpClient.SendAsync(requestBytes, requestBytes.Length, broadcastEndpoint);

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                // Zaman aşımına kadar dinle; IdleBoard olmayan veya bozuk paketler aramayı bitirmez
                while (true)
                {
                    var result = await udpClient.ReceiveAsync(linkedCts.Token);
                    var server = TryParseServerReply(result);
                    if (server != null) return server;
                }
            }
            catch (OperationCanceledException)
            {
                // Zaman aşımı - ağda sunucu yanıt vermedi
            }
            catch (Exception ex)
            {
                Logger.LogError("[Ağ Keşfi] Otomatik arama sırasında hata", ex);
            }

            return null;
        }

        private static DiscoveredServer? TryParseServerReply(UdpReceiveResult result)
        {
            try
            {
                string jsonStr = Encoding.UTF8.GetString(result.Buffer);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("service", out var sProp) &&
                    sProp.ValueKind == JsonValueKind.String && sProp.GetString() == "idleboard-server")
                {
                    var addr = result.RemoteEndPoint.Address;
                    if (addr.IsIPv4MappedToIPv6)
                    {
                        addr = addr.MapToIPv4();
                    }
                    string responderIp = addr.ToString().Replace("::ffff:", "").Trim();

                    int port = root.TryGetProperty("httpPort", out var pProp) && pProp.ValueKind == JsonValueKind.Number ? pProp.GetInt32() : 3000;
                    string hostname = root.TryGetProperty("hostname", out var hProp) && hProp.ValueKind == JsonValueKind.String ? hProp.GetString() ?? "" : "";

                    // Sunucunun kendi bildirdiği primaryIp geçerli ve loopback değilse öncelikle onu değerlendir
                    string serverIp = responderIp;
                    if (root.TryGetProperty("primaryIp", out var pIpEl) && pIpEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(pIpEl.GetString()) &&
                        pIpEl.GetString() != "127.0.0.1")
                    {
                        serverIp = pIpEl.GetString()!.Trim();
                    }

                    string baseUri = $"http://{serverIp}:{port}";
                    string eventsUri = $"{baseUri}/events";

                    Logger.LogInfo($"[Ağ Keşfi] Sunucu başarıyla bulundu: {baseUri} ({hostname})");
                    return new DiscoveredServer(baseUri, eventsUri, serverIp, port, hostname);
                }
            }
            catch (Exception)
            {
                // Geçersiz paket: yok say
            }

            return null;
        }

        /// <summary>
        /// Kurulum dosyası veya kayıt defteri tarafından belirlenmiş kayıtlı sunucu adresini okur.
        /// </summary>
        public static string? GetConfiguredServerUrl()
        {
            try
            {
                // 1. Kurulum sihirbazının yazdığı client-config.json dosyasını kontrol et
                string configPath = Path.Combine(AppContext.BaseDirectory, "client-config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ServerUrl", out var sProp))
                    {
                        string val = sProp.GetString()?.Trim() ?? "";
                        if (IsValidServerUrl(val))
                        {
                            return NormalizeEventsUrl(val);
                        }
                    }
                }

                // 2. Windows Kayıt Defteri (Registry) değerini kontrol et
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\IdleBoardClient");
                if (key != null)
                {
                    var regVal = key.GetValue("ServerUrl") as string;
                    if (!string.IsNullOrWhiteSpace(regVal) && IsValidServerUrl(regVal))
                    {
                        return NormalizeEventsUrl(regVal);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[Yapılandırma] Kayıtlı sunucu adresi okunurken hata", ex);
            }

            return null;
        }

        public static bool IsValidServerUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.Contains("ServerIp", StringComparison.OrdinalIgnoreCase)) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        public static string NormalizeEventsUrl(string url)
        {
            url = url.Trim().TrimEnd('/');
            if (!url.EndsWith("/events", StringComparison.OrdinalIgnoreCase))
            {
                url += "/events";
            }
            return url;
        }
    }
}
