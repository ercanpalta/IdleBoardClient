using IdleBoardClient;
using System;
using System.Globalization;
using System.Windows.Controls;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
// Forms ve WPF Isim Çakışmasını Önlemek İçin Alias
using Forms = System.Windows.Forms;
using Image = System.Windows.Controls.Image;
using Border = System.Windows.Controls.Border;

namespace SseWpfApp
{
    public partial class MainWindow : Window
    {
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Kurulum sihirbazından veya Registry'den gelen adresi kontrol et
            string? configuredUrl = ServerDiscoveryService.GetConfiguredServerUrl();
            if (!string.IsNullOrWhiteSpace(configuredUrl))
            {
                ServerUrl = configuredUrl;
                Settings.Default.ServerUrl = ServerUrl;
                Settings.Default.Save();
            }

            // 2. İlk açılışta yükleniyor / eşitleme ekranını göster
            LoadingOverlay.Visibility = System.Windows.Visibility.Visible;
            TxtLoadingTitle.Text = "Sunucu Aranıyor...";
            TxtLoadingStatus.Text = "Ağdaki IdleBoard sunucusuna bağlanılıyor...";

            // 3. Eğer mevcut URL localhost ise veya geçersiz ise, yerel ağda sunucuyu otomatik bulmayı dene
            bool isLocalhost = ServerUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                               ServerUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

            if (isLocalhost || !ServerDiscoveryService.IsValidServerUrl(ServerUrl))
            {
                TxtLoadingTitle.Text = "Ağ Taranıyor...";
                TxtLoadingStatus.Text = "Yerel ağdaki IdleBoard sunucusu otomatik aranıyor...";

                var discovered = await ServerDiscoveryService.DiscoverServerAsync(TimeSpan.FromSeconds(2.0));
                if (discovered != null)
                {
                    ServerUrl = discovered.EventsUrl;
                    Settings.Default.ServerUrl = ServerUrl;
                    Settings.Default.Save();
                    Logger.LogInfo($"[Otomatik Keşif] Sunucu bulundu ve bağlandı: {ServerUrl}");
                    TxtLoadingStatus.Text = $"Sunucu bulundu: {discovered.ServerUrl}. Veriler eşitleniyor...";
                }
            }

            // 4. Bağlantıyı başlat
            SseBaslat();

            // 5. Sunucuya ulaşılamama durumunda yerel diskteki önbellekten devam etme zaman aşımı
            BaslatBaglantiZamanAsimiTakibi();
        }

        private void BaslatBaglantiZamanAsimiTakibi()
        {
            var timeoutTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeoutTimer.Tick += async (s, e) =>
            {
                timeoutTimer.Stop();
                if (!_isInitialSyncCompleted)
                {
                    // Bağlantı kurulamadıysa, ağda başka/yeni bir sunucu var mı diye bir kez daha tara
                    var discovered = await ServerDiscoveryService.DiscoverServerAsync(TimeSpan.FromSeconds(2.0));
                    if (discovered != null && discovered.EventsUrl != ServerUrl)
                    {
                        ServerUrl = discovered.EventsUrl;
                        Settings.Default.ServerUrl = ServerUrl;
                        Settings.Default.Save();
                        Logger.LogInfo($"[Zaman Aşımı Kurtarma] Ağdaki yeni sunucuya bağlanılıyor: {ServerUrl}");
                        SseBaslat();
                        return;
                    }

                    if (ImageCacheManager.GetCachedCount() > 0)
                    {
                        TxtLoadingTitle.Text = "Çevrimdışı Mod";
                        TxtLoadingStatus.Text = "Sunucuya ulaşılamadı. Yerel diskteki verilerle başlatılıyor...";

                        var hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                        hideTimer.Tick += (hs, he) =>
                        {
                            hideTimer.Stop();
                            if (!_isInitialSyncCompleted)
                            {
                                SonrakiOfflineGorseliniGoster();
                                EnsureOfflineSlideTimerRunning();
                                LoadingOverlay.Visibility = System.Windows.Visibility.Collapsed;
                            }
                        };
                        hideTimer.Start();
                    }
                    else
                    {
                        // Önbellekte de veri yoksa manuel giriş ekranını aç!
                        bool ayarlandi = IlkAcalistaUrlSor();
                        if (ayarlandi)
                        {
                            SseBaslat();
                        }
                        else
                        {
                            TxtLoadingTitle.Text = "Bağlantı Kurulamadı";
                            TxtLoadingStatus.Text = "Sunucuya bağlanılamadı. Lütfen sunucu adresini kontrol edin.\n(Ayarlar için Ctrl + Shift + S tuşlarına basabilirsiniz)";
                        }
                    }
                }
            };
            timeoutTimer.Start();
        }

        private async Task BaslangicEsitlemesiniYapAsync(IReadOnlyList<string> activeList, CancellationToken ct)
        {
            if (_isInitialSyncCompleted) return;

            Dispatcher.Invoke(() =>
            {
                TxtLoadingTitle.Text = "Veriler alınıyor...";
                TxtLoadingStatus.Text = $"{activeList.Count} adet medya dosyası sunucu ile eşitleniyor...";
            });

            await ImageCacheManager.SyncActiveImagesAsync(ServerUrl, activeList, (current, total, file) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TxtLoadingStatus.Text = $"Dosyalar eşitleniyor ({current}/{total}): {file}";
                }));
            }, ct);

            _isInitialSyncCompleted = true;

            // Eşitleme bittiğinde sunucu yayın akışında o an hangi görsel/video varsa doğrudan onu diskten ekrana getir
            bool gosterildi = false;
            if (!string.IsNullOrEmpty(_sonSunucuYayinDosyasi))
            {
                if (_sonSunucuYayinVideoMu)
                {
                    Dispatcher.Invoke(() => VideoyuGuncelle(_sonSunucuYayinDosyasi, _videoStartOffsetSec));
                    gosterildi = true;
                }
                else
                {
                    gosterildi = await ResmiGuncelleDosyadanAsync(_sonSunucuYayinDosyasi, _sonSunucuYayinBase64);
                }
            }

            if (!gosterildi && _isOffline)
            {
                Dispatcher.Invoke(() => SonrakiOfflineGorseliniGoster());
            }

            // Yükleme ekranını yumuşak geçişle kapat
            Dispatcher.Invoke(() =>
            {
                var fadeAnimation = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350));
                fadeAnimation.Completed += (s, ev) =>
                {
                    LoadingOverlay.Visibility = System.Windows.Visibility.Collapsed;
                };
                LoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
            });
        }

        private async Task<bool> ResmiGuncelleDosyadanAsync(string dosyaAdi, string? base64Fallback)
        {
            try
            {
                // Önce görselin yerel diskte olduğundan emin ol (Canlı yayından doğrudan oynatma ASLA yapılmaz)
                string? localPath = await ImageCacheManager.EnsureFileDownloadedAsync(ServerUrl, dosyaAdi);

                if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                {
                    var bmp = ImageHelper.LoadBitmapFromFile(localPath);
                    if (bmp != null)
                    {
                        ResmiGuncelle(bmp);
                        return true;
                    }
                }

                // Eğer indirme başarısız olduysa ama SSE içinde base64 verisi geldiyse
                if (!string.IsNullOrEmpty(base64Fallback))
                {
                    string? savedPath = await ImageCacheManager.SaveImageAsync(dosyaAdi, base64Fallback);
                    if (!string.IsNullOrEmpty(savedPath) && System.IO.File.Exists(savedPath))
                    {
                        var bmp = ImageHelper.LoadBitmapFromFile(savedPath);
                        if (bmp != null)
                        {
                            ResmiGuncelle(bmp);
                            return true;
                        }
                    }
                    ResmiGuncelle(base64Fallback);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Görsel] Dosyadan görsel yüklenirken hata: {dosyaAdi}", ex);
            }
            return false;
        }

        private async void ResmiGuncelleDosyadan(string dosyaAdi, string? base64Fallback)
        {
            await ResmiGuncelleDosyadanAsync(dosyaAdi, base64Fallback);
        }

        private void SseBaslat()
        {
            // Varsa eski iptal jetonunu temizle ve yenisini oluştur
            _sseCancellationTokenSource?.Cancel();
            _sseCancellationTokenSource = new CancellationTokenSource();

            var token = _sseCancellationTokenSource.Token;
            Task.Run(async () => await StartSseListenerAsync(token), token);
        }

        // İlk açılışa özel Ayarlar Penceresi diyalog yönetimi
        private bool IlkAcalistaUrlSor()
        {
            var settingsWin = new SettingsWindow("http://localhost:3000/events");

            // Pencereyi öne getir
            settingsWin.Topmost = true;

            if (settingsWin.ShowDialog() == true)
            {
                ServerUrl = settingsWin.ServerUrl;

                // Ayarlara kaydet
                Settings.Default.ServerUrl = ServerUrl;
                Settings.Default.Save();
                return true;
            }

            return false;
        }

        private async Task StartSseListenerAsync(CancellationToken cancellationToken)
        {
            // Yeniden bağlanma süreleri (saniye): 2s, 4s, 8s, 16s ve maksimum 30s
            int retryDelaySeconds = 2;
            const int maxRetryDelaySeconds = 30;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 1. Durum: Bağlantı Kuruluyor (Sarı İkon)
                    DurumIconGuncelle(BaglantiDurumu.Baglaniyor, "Sunucuya istek atılıyor...");

                    using var client = new HttpClient();
                    // SSE akışı için HttpClient zaman aşımını sınırsız yapıyoruz
                    client.Timeout = Timeout.InfiniteTimeSpan;

                    string sseUrl = ServerUrl;
                    string appVer = AutoUpdater.CurrentVersion;
                    sseUrl += (sseUrl.Contains('?') ? "&" : "?") + "clientVersion=" + Uri.EscapeDataString(appVer);

                    using var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);

                    EkranaYazdir($"[{ServerUrl}] adresine bağlantı aktif. Resimler alınıyor...");
                    SetOfflineMode(false);
                    // 2. Durum: Bağlantı Başarılı (Yeşil İkon)
                    DurumIconGuncelle(BaglantiDurumu.Bagli);

                    // Başarılı bağlantı kurulduğunda bekleme süresini varsayılana sıfırla
                    retryDelaySeconds = 2;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        TimeSpan streamReadTimeout = TimeSpan.FromSeconds(90);
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        readCts.CancelAfter(streamReadTimeout);

                        string? line;
                        try
                        {
                            line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            Logger.LogInfo("SSE akışında veri zaman aşımı (bağlantı koptu). Çevrimdışı moda geçiliyor.");
                            SetOfflineMode(true);
                            break;
                        }

                        if (line == null) // end of stream
                        {
                            Logger.LogInfo("Sunucu bağlantısı kapandı. Çevrimdışı moda geçiliyor.");
                            SetOfflineMode(true);
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        // Sunucudan gelen keep-alive / ping yorum satırlarını işle
                        if (line.StartsWith(":", StringComparison.Ordinal))
                            continue;

                        // Event Stream standardına uygun "data: " satırlarını işle
                        if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                        {
                            string jsonString = line.Substring(6).Trim();

                            try
                            {
                                using var doc = JsonDocument.Parse(jsonString);
                                var root = doc.RootElement;

                                string tip = "";
                                if (root.TryGetProperty("tip", out var tipElement))
                                {
                                    tip = tipElement.GetString() ?? "";
                                }

                                if (tip == "stream_control")
                                {
                                    if (root.TryGetProperty("action", out var actionElement))
                                    {
                                        string action = actionElement.GetString() ?? "";
                                        if (action == "stop" || action == "pause")
                                        {
                                            SetStreamStopped(true);
                                        }
                                        else if (action == "clear")
                                        {
                                            ClearLiveImage();
                                        }
                                        else if (action == "start" || action == "resume")
                                        {
                                            SetStreamStopped(false);
                                        }
                                    }
                                    continue;
                                }
                                else if (tip == "pin_control")
                                {
                                    bool pinned = root.TryGetProperty("isPinned", out var pinEl) && pinEl.GetBoolean();
                                    _isStreamPinned = pinned;
                                    Logger.LogInfo($"[Pin] Sunucudan sabitleme talimatı alındı: {pinned}");

                                    if (pinned)
                                    {
                                        if (root.TryGetProperty("fileName", out var fnEl) && !string.IsNullOrEmpty(fnEl.GetString()))
                                        {
                                            string pFile = fnEl.GetString()!;
                                            bool ayniMedya = string.Equals(_sonSunucuYayinDosyasi, pFile, StringComparison.OrdinalIgnoreCase);
                                            _sonSunucuYayinDosyasi = pFile;
                                            bool isVid = root.TryGetProperty("isVideo", out var vEl) ? vEl.GetBoolean() : ImageCacheManager.IsVideoFile(pFile);
                                            _sonSunucuYayinVideoMu = isVid;

                                            if (_isInitialSyncCompleted && !ayniMedya)
                                            {
                                                Dispatcher.Invoke(() =>
                                                {
                                                    if (isVid) VideoyuGuncelle(pFile);
                                                    else ResmiGuncelleDosyadan(pFile, null);
                                                });
                                            }
                                            else if (_isInitialSyncCompleted && ayniMedya && isVid)
                                            {
                                                Dispatcher.Invoke(() =>
                                                {
                                                    if (mediaVideo.Visibility == Visibility.Visible)
                                                    {
                                                        mediaVideo.Play();
                                                    }
                                                });
                                            }
                                        }
                                    }
                                    continue;
                                }
                                else if (tip == "stream_status")
                                {
                                    if (root.TryGetProperty("isPinned", out var isPinEl))
                                    {
                                        _isStreamPinned = isPinEl.GetBoolean();
                                    }
                                    if (root.TryGetProperty("isPaused", out var isPauseEl))
                                    {
                                        SetStreamStopped(isPauseEl.GetBoolean());
                                    }
                                    if (root.TryGetProperty("currentImage", out var curImgEl) && !string.IsNullOrEmpty(curImgEl.GetString()))
                                    {
                                        string cFile = curImgEl.GetString()!;
                                        bool isVid = root.TryGetProperty("isVideo", out var ivEl) ? ivEl.GetBoolean() : ImageCacheManager.IsVideoFile(cFile);
                                        
                                        if (!string.Equals(_sonSunucuYayinDosyasi, cFile, StringComparison.OrdinalIgnoreCase))
                                        {
                                            _sonSunucuYayinDosyasi = cFile;
                                            _sonSunucuYayinVideoMu = isVid;

                                            if (_isInitialSyncCompleted)
                                            {
                                                Dispatcher.Invoke(() =>
                                                {
                                                    if (isVid) VideoyuGuncelle(cFile);
                                                    else ResmiGuncelleDosyadan(cFile, null);
                                                });
                                            }
                                        }
                                    }
                                    continue;
                                }
                                else if (tip == "update_command")
                                {
                                    string? targetVer = root.TryGetProperty("targetVersion", out var tvEl) ? tvEl.GetString() : null;
                                    string? dlUrl = root.TryGetProperty("downloadUrl", out var dlEl) ? dlEl.GetString() : null;
                                    if (!string.IsNullOrWhiteSpace(dlUrl))
                                    {
                                        EkranaYazdir($"[GÜNCELLEME] Sunucudan güncelleme talimatı alındı ({targetVer}). Yükleme başlatılıyor...");
                                        _ = AutoUpdater.ApplyUpdateAsync(dlUrl, targetVer ?? "latest");
                                    }
                                    continue;
                                }
                                else if (tip == "init")
                                {
                                    if (root.TryGetProperty("isPaused", out var isPausedElement))
                                    {
                                        SetStreamStopped(isPausedElement.GetBoolean());
                                    }
                                    if (root.TryGetProperty("isPinned", out var isPinEl))
                                    {
                                        _isStreamPinned = isPinEl.GetBoolean();
                                        Logger.LogInfo($"[Init] Sunucu sabitleme durumu: {_isStreamPinned}");
                                    }
                                    if (root.TryGetProperty("currentImage", out var curImgEl) && !string.IsNullOrEmpty(curImgEl.GetString()))
                                    {
                                        _sonSunucuYayinDosyasi = curImgEl.GetString();
                                        _sonSunucuYayinVideoMu = root.TryGetProperty("isVideo", out var ivEl) && ivEl.GetBoolean();
                                        _videoStartOffsetSec = 0;
                                        if (_sonSunucuYayinVideoMu
                                            && root.TryGetProperty("broadcastAt", out var initBatEl) && initBatEl.ValueKind == JsonValueKind.Number
                                            && root.TryGetProperty("serverNow", out var srvNowEl) && srvNowEl.ValueKind == JsonValueKind.Number)
                                        {
                                            long initBat = initBatEl.GetInt64();
                                            long srvNow = srvNowEl.GetInt64();
                                            double vidDur = root.TryGetProperty("mediaDuration", out var mdEl) && mdEl.ValueKind == JsonValueKind.Number ? mdEl.GetDouble() : 0;
                                            if (initBat > 0 && srvNow >= initBat)
                                            {
                                                double diffSec = (srvNow - initBat) / 1000.0;
                                                _videoStartOffsetSec = (vidDur > 1.0 && diffSec >= vidDur - 1.0) ? 0 : diffSec;
                                            }
                                        }
                                        Logger.LogInfo($"[Init] Sunucu anlık yayın medyası: {_sonSunucuYayinDosyasi} (Video: {_sonSunucuYayinVideoMu}, Sabitli: {_isStreamPinned}, Offset: {_videoStartOffsetSec:0.0}s)");
                                    }
                                }

                                if (root.TryGetProperty("slideInterval", out var intervalElement)
                                    && intervalElement.ValueKind == JsonValueKind.Number)
                                {
                                    UygulaSlideInterval(intervalElement.GetDouble());
                                }

                                if ((root.TryGetProperty("displayIdleTimeout", out var idleElement) || root.TryGetProperty("idleTimeout", out idleElement) || root.TryGetProperty("kioskIdleTimeout", out idleElement))
                                    && idleElement.ValueKind == JsonValueKind.Number)
                                {
                                    UygulaBostaKalmaZamanAsimi(idleElement.GetInt32());
                                }

                                if (root.TryGetProperty("imageFit", out var fitElement)
                                    && fitElement.ValueKind == JsonValueKind.String)
                                {
                                    UygulaImageFit(fitElement.GetString());
                                }

                                if (root.TryGetProperty("showInfoBar", out var infoBarElement)
                                    && (infoBarElement.ValueKind == JsonValueKind.True || infoBarElement.ValueKind == JsonValueKind.False))
                                {
                                    UygulaShowInfoBar(infoBarElement.GetBoolean());
                                }

                                if (root.TryGetProperty("transitionEffect", out var transitionElement)
                                    && transitionElement.ValueKind == JsonValueKind.String)
                                {
                                    UygulaTransitionEffect(transitionElement.GetString());
                                }

                                if (root.TryGetProperty("slideshowOrder", out var orderElement)
                                    && orderElement.ValueKind == JsonValueKind.String)
                                {
                                    UygulaSlideshowOrder(orderElement.GetString());
                                }

                                if (root.TryGetProperty("showWatermark", out var watermarkVisElement)
                                    && (watermarkVisElement.ValueKind == JsonValueKind.True || watermarkVisElement.ValueKind == JsonValueKind.False))
                                {
                                    UygulaShowWatermark(watermarkVisElement.GetBoolean());
                                }

                                if (root.TryGetProperty("watermarkImage", out var watermarkImgElement))
                                {
                                    if (watermarkImgElement.ValueKind == JsonValueKind.String)
                                    {
                                        string b64 = watermarkImgElement.GetString() ?? string.Empty;
                                        UygulaWatermarkImage(b64);
                                    }
                                    else if (watermarkImgElement.ValueKind == JsonValueKind.Null)
                                    {
                                        UygulaWatermarkImage(null);
                                    }
                                }

                                if (root.TryGetProperty("activeImages", out var activeImagesEl) && activeImagesEl.ValueKind == JsonValueKind.Array)
                                {
                                    var activeList = new List<string>();
                                    foreach (var item in activeImagesEl.EnumerateArray())
                                    {
                                        if (item.ValueKind == JsonValueKind.String)
                                        {
                                            string? name = item.GetString();
                                            if (!string.IsNullOrWhiteSpace(name))
                                            {
                                                activeList.Add(name);
                                            }
                                        }
                                    }
                                    if (!_isInitialSyncCompleted)
                                    {
                                        if (!_isInitialSyncRunning)
                                        {
                                            _isInitialSyncRunning = true;
                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    await BaslangicEsitlemesiniYapAsync(activeList, cancellationToken);
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogError("Başlangıç eşitlemesi hatası", ex);
                                                }
                                                finally
                                                {
                                                    _isInitialSyncRunning = false;
                                                }
                                            }, cancellationToken);
                                        }
                                    }
                                    else
                                    {
                                        _ = ImageCacheManager.SyncActiveImagesAsync(ServerUrl, activeList, null, cancellationToken);
                                    }
                                }

                                string? dosyaAdi = null;
                                if (root.TryGetProperty("dosyaAdi", out var dosyaElement))
                                {
                                    dosyaAdi = dosyaElement.GetString();
                                }

                                bool isVideo = false;
                                if (root.TryGetProperty("mediaType", out var mtElement) && mtElement.GetString() == "video")
                                {
                                    isVideo = true;
                                }
                                else if (!string.IsNullOrEmpty(dosyaAdi) && ImageCacheManager.IsVideoFile(dosyaAdi))
                                {
                                    isVideo = true;
                                }

                                string? resimBase64Data = root.TryGetProperty("resimData", out var rDataEl) ? rDataEl.GetString() : null;

                                // Sunucudan gelen yayın akışı bilgisini hafızada tut
                                if (!string.IsNullOrEmpty(dosyaAdi) || !string.IsNullOrEmpty(resimBase64Data))
                                {
                                    _sonSunucuYayinDosyasi = dosyaAdi;
                                    _sonSunucuYayinVideoMu = isVideo;
                                    _sonSunucuYayinBase64 = resimBase64Data;
                                }

                                bool isIncomingPinned = root.TryGetProperty("isPinned", out var imgPinnedEl) && imgPinnedEl.GetBoolean();

                                // Eğer ekranda sabitleme aktifse ve gelen mesaj sabitlenmiş bir medya DEĞİLSE:
                                // Rutin slayt geçişini yoksay ve ekrandaki sabitlenmiş medyayı koru!
                                if (_isStreamPinned && !isIncomingPinned)
                                {
                                    continue;
                                }

                                _isStreamPinned = isIncomingPinned;

                                // İlk açılış eşitlemesi bitene kadar tekil medya oynatmayı beklet
                                if (!_isInitialSyncCompleted)
                                {
                                    continue;
                                }

                                if (isVideo && !string.IsNullOrEmpty(dosyaAdi))
                                {
                                    VideoyuGuncelle(dosyaAdi, 0);
                                }
                                else if (!string.IsNullOrEmpty(dosyaAdi))
                                {
                                    ResmiGuncelleDosyadan(dosyaAdi, resimBase64Data);
                                }
                                else if (!string.IsNullOrEmpty(resimBase64Data))
                                {
                                    ResmiGuncelle(resimBase64Data);
                                    _ = ImageCacheManager.SaveImageAsync(dosyaAdi, resimBase64Data);
                                }
                            }
                            catch (JsonException ex)
                            {
                                EkranaYazdir($"[JSON PARSE HATASI] {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // İsteğe bağlı iptal (URL değişimi veya uygulama kapanışı)
                    EkranaYazdir("SSE bağlantı döngüsü sonlandırıldı.");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    // 3. Durum: Bağlantı Kopması / Hata (Kırmızı İkon)
                    SetOfflineMode(true);
                    DurumIconGuncelle(BaglantiDurumu.Koptu, ex.Message);
                    EkranaYazdir($"[AĞ HATASI] Sunucuya ulaşılamıyor: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // 3. Durum: Bağlantı Kopması / Hata (Kırmızı İkon)
                    SetOfflineMode(true);
                    DurumIconGuncelle(BaglantiDurumu.Koptu, ex.Message);
                    Logger.LogError("SSE Bağlantı Hatası Oluştu", ex);
                    EkranaYazdir($"[BEKLENMEYEN HATA] {ex.Message}");
                }

                // İptal edilmediyse ve akış koptuysa katlanarak bekle ve tekrar dene
                if (!cancellationToken.IsCancellationRequested)
                {
                    DurumIconGuncelle(BaglantiDurumu.Baglaniyor, $"{retryDelaySeconds}sn sonra tekrar denenecek");
                    EkranaYazdir($"Bağlantı koptu. {retryDelaySeconds} saniye sonra tekrar deneniyor...");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Bekleme süresini 2 katına çıkar (Maksimum 30 saniye)
                    retryDelaySeconds = Math.Min(retryDelaySeconds * 2, maxRetryDelaySeconds);
                }
            }
        }
    }
}
