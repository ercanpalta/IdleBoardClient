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
            // 1. Kurulum sihirbazından veya Registry'den gelen adresi yalnızca henüz bir adres
            // kaydedilmemişse (veya varsayılan localhost ise) uygula; kullanıcının ayarlardan girdiği adres ezilmesin
            string? configuredUrl = ServerDiscoveryService.GetConfiguredServerUrl();
            bool userUrlMissing = string.IsNullOrWhiteSpace(Settings.Default.ServerUrl)
                || Settings.Default.ServerUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || Settings.Default.ServerUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(configuredUrl) && userUrlMissing)
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
                if (!_isInitialSyncCompleted && _isInitialSyncRunning)
                {
                    // Sunucuya bağlanıldı ve dosyalar indiriliyor; yavaş indirme çevrimdışı mod sayılmaz
                    BaslatBaglantiZamanAsimiTakibi();
                    return;
                }
                if (!_isInitialSyncCompleted)
                {
                    // Bağlantı kurulamadıysa, ağda başka/yeni bir sunucu var mı diye bir kez daha tara
                    var discovered = await ServerDiscoveryService.DiscoverServerAsync(TimeSpan.FromSeconds(2.0));
                    if (discovered != null && discovered.EventsUrl != ServerUrl)
                    {
                        ServerUrl = discovered.EventsUrl;
                        // Zaman aşımı kurtarmasında bulunan adres kalıcı yazılmaz; bağlantı başarılı olursa kullanıcı ayarı değişmeden kalır
                        Logger.LogInfo($"[Zaman Aşımı Kurtarma] Ağdaki yeni sunucuya bağlanılıyor: {ServerUrl}");
                        SseBaslat();
                        // Yeni adres de yanıt vermezse yükleme ekranı sonsuza dek kalmasın
                        BaslatBaglantiZamanAsimiTakibi();
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

            bool syncOk = await ImageCacheManager.SyncActiveImagesAsync(ServerUrl, activeList, (current, total, file) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TxtLoadingStatus.Text = $"Dosyalar eşitleniyor ({current}/{total}): {file}";
                }));
            }, ct);

            if (ct.IsCancellationRequested) return;

            // Bazı dosyalar indirilemese bile yayın akışı başlatılır; eksik dosyalar her sunucu mesajında arka planda
            // yeniden denenir ve gösterim anında talep üzerine indirilir. Aksi halde ekran tek görselde takılı kalır.
            _isInitialSyncCompleted = true;
            if (!syncOk) Logger.LogInfo("[Eşitleme] Bazı dosyalar indirilemedi, arka planda yeniden denenecek; yayın akışı başlatıldı.");

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
            // Video ile ortak sürüm sayacı: bu istek sürerken daha yeni bir video/görsel istenirse sonuç uygulanmaz
            int version = Interlocked.Increment(ref _videoPlayVersion);
            try
            {
                // Önce görselin yerel diskte olduğundan emin ol (Canlı yayından doğrudan oynatma ASLA yapılmaz)
                string? localPath = await ImageCacheManager.EnsureFileDownloadedAsync(ServerUrl, dosyaAdi);
                if (version != _videoPlayVersion) return true;
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
                    if (version != _videoPlayVersion) return true;
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
            var eskiCts = _sseCancellationTokenSource;
            eskiCts?.Cancel();
            _sseCancellationTokenSource = new CancellationTokenSource();
            eskiCts?.Dispose();

            var token = _sseCancellationTokenSource.Token;
            Task.Run(async () => await StartSseListenerAsync(token), token);
        }

        // Pencere herhangi bir yolla kapanırsa (oturum kapatma dahil) SSE, zamanlayıcılar ve tray ikonu temizlenir
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _sseCancellationTokenSource?.Cancel();
                _clockTimer?.Stop();
                _idleTimer?.Stop();
                _slideTimer?.Stop();
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                foreach (var icon in _durumIkonlari.Values) icon.Dispose();
                _durumIkonlari.Clear();
            }
            catch (Exception ex)
            {
                Logger.LogError("Pencere kapanırken kaynaklar temizlenemedi.", ex);
            }
            base.OnClosed(e);
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

                    // Bekleme süresi, bağlantı en az 10 sn ayakta kalıp veri akınca sıfırlanır (kısa ömürlü bağlantılar geri çekilmeyi bozmasın)
                    var baglantiSayaci = System.Diagnostics.Stopwatch.StartNew();
                    bool beklemeSifirlandi = false;

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

                        if (!beklemeSifirlandi && baglantiSayaci.Elapsed >= TimeSpan.FromSeconds(10))
                        {
                            beklemeSifirlandi = true;
                            retryDelaySeconds = 2;
                        }
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
                                    if (root.TryGetProperty("mediaDuration", out var pmdEl) && pmdEl.ValueKind == JsonValueKind.Number)
                                    {
                                        double pmd = pmdEl.GetDouble();
                                        if (pmd > 0.5) _aktifVideoSuresiSn = Math.Max(_aktifVideoSuresiSn, pmd);
                                    }

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
                                                        double durationSec = Math.Max(_aktifVideoSuresiSn, (mediaVideo.NaturalDuration.HasTimeSpan ? mediaVideo.NaturalDuration.TimeSpan.TotalSeconds : 0));
                                                        bool sonaUlasti = durationSec > 0 && mediaVideo.Position.TotalSeconds >= (durationSec - 0.3);
                                                        if (sonaUlasti)
                                                        {
                                                            mediaVideo.Position = TimeSpan.Zero;
                                                        }
                                                        mediaVideo.Play();
                                                        EnsureVideoWatchdogRunning();
                                                    }
                                                    else
                                                    {
                                                        VideoyuGuncelle(pFile);
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
                                    double serverDur = 0;
                                    if (root.TryGetProperty("activeDuration", out var adEl) && adEl.ValueKind == JsonValueKind.Number)
                                    {
                                        serverDur = adEl.GetDouble();
                                    }
                                    else if (root.TryGetProperty("mediaDuration", out var smdEl) && smdEl.ValueKind == JsonValueKind.Number)
                                    {
                                        serverDur = smdEl.GetDouble();
                                    }
                                    if (serverDur > 0.5)
                                    {
                                        _aktifVideoSuresiSn = Math.Max(_aktifVideoSuresiSn, serverDur);
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
                                                    if (isVid) VideoyuGuncelle(cFile, 0, serverDur);
                                                    else ResmiGuncelleDosyadan(cFile, null);
                                                });
                                            }
                                        }
                                        else if (_isStreamPinned && isVid && _isInitialSyncCompleted)
                                        {
                                            Dispatcher.Invoke(() =>
                                            {
                                                if (mediaVideo.Visibility == Visibility.Visible)
                                                {
                                                    double durationSec = Math.Max(_aktifVideoSuresiSn, (mediaVideo.NaturalDuration.HasTimeSpan ? mediaVideo.NaturalDuration.TimeSpan.TotalSeconds : 0));
                                                    bool sonaUlasti = durationSec > 0 && mediaVideo.Position.TotalSeconds >= (durationSec - 0.3);
                                                    if (sonaUlasti)
                                                    {
                                                        mediaVideo.Position = TimeSpan.Zero;
                                                    }
                                                    mediaVideo.Play();
                                                    EnsureVideoWatchdogRunning();
                                                }
                                            });
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
                                else if (tip == "active_images")
                                {
                                    if (root.TryGetProperty("activeImages", out var activeMsgImagesEl) && activeMsgImagesEl.ValueKind == JsonValueKind.Array)
                                    {
                                        var activeList = new List<string>();
                                        foreach (var item in activeMsgImagesEl.EnumerateArray())
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
                                        if (activeList.Count > 0)
                                        {
                                            Logger.LogInfo($"[Önbellek] Sunucudan aktif galeri listesi güncellendi ({activeList.Count} dosya). Arka planda indirme başlatılıyor...");
                                            if (_isInitialSyncCompleted)
                                            {
                                                _ = ImageCacheManager.SyncActiveImagesAsync(ServerUrl, activeList, null, cancellationToken);
                                            }
                                        }
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
                                        string? oncekiYayin = _sonSunucuYayinDosyasi;
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

                                        // Yeniden bağlanmada eşitleme zaten tamamlanmıştır; sunucudaki güncel medya burada gösterilmelidir
                                        string? yeniYayin = _sonSunucuYayinDosyasi;
                                        bool yeniVideoMu = _sonSunucuYayinVideoMu;
                                        double yeniOffset = _videoStartOffsetSec;
                                        if (_isInitialSyncCompleted && !string.IsNullOrEmpty(yeniYayin)
                                            && !string.Equals(oncekiYayin, yeniYayin, StringComparison.OrdinalIgnoreCase))
                                        {
                                            Dispatcher.Invoke(() =>
                                            {
                                                if (yeniVideoMu) VideoyuGuncelle(yeniYayin, yeniOffset);
                                                else ResmiGuncelleDosyadan(yeniYayin, null);
                                            });
                                        }
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
                                    UygulaBostaKalmaZamanAsimi(idleElement.TryGetInt32(out int idleSn) ? idleSn : (int)Math.Round(idleElement.GetDouble()));
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

                                bool isIncomingPinned = root.TryGetProperty("isPinned", out var imgPinnedEl) && imgPinnedEl.GetBoolean();

                                // Eğer ekranda sabitleme aktifse ve gelen mesaj sabitlenmiş bir medya DEĞİLSE:
                                // Rutin slayt geçişini yoksay ve ekrandaki sabitlenmiş medyayı koru!
                                if (_isStreamPinned && !isIncomingPinned)
                                {
                                    continue;
                                }

                                // Sunucudan gelen yayın akışı bilgisini hafızada tut (yoksayılan slayt sabitli medyanın üzerine yazmasın)
                                if (!string.IsNullOrEmpty(dosyaAdi) || !string.IsNullOrEmpty(resimBase64Data))
                                {
                                    _sonSunucuYayinDosyasi = dosyaAdi;
                                    _sonSunucuYayinVideoMu = isVideo;
                                    _sonSunucuYayinBase64 = resimBase64Data;
                                }

                                _isStreamPinned = isIncomingPinned;

                                // İlk açılış eşitlemesi bitene kadar tekil medya oynatmayı beklet
                                if (!_isInitialSyncCompleted)
                                {
                                    continue;
                                }

                                if (isVideo && !string.IsNullOrEmpty(dosyaAdi))
                                {
                                    double serverDur = 0;
                                    if (root.TryGetProperty("videoDuration", out var vdEl) && vdEl.ValueKind == JsonValueKind.Number)
                                    {
                                        serverDur = vdEl.GetDouble();
                                    }
                                    else if (root.TryGetProperty("mediaDuration", out var mdEl) && mdEl.ValueKind == JsonValueKind.Number)
                                    {
                                        serverDur = mdEl.GetDouble();
                                    }
                                    VideoyuGuncelle(dosyaAdi, 0, serverDur);
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
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // Hatalı tipli/eksik alan tek bir mesajı atlatır; SSE bağlantısı kopmaz
                                EkranaYazdir($"[MESAJ İŞLEME HATASI] {ex.GetType().Name}: {ex.Message}");
                                Logger.LogError("SSE mesajı işlenirken hata oluştu.", ex);
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
