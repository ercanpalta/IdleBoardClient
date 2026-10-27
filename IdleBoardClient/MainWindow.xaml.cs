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
        private DispatcherTimer _clockTimer = null!;

        // Varsayılan adresi kaydedilen Settings ayarlarından çekiyoruz
        private string ServerUrl = !string.IsNullOrWhiteSpace(Settings.Default.ServerUrl) ? Settings.Default.ServerUrl : "http://localhost:3000/events";
        // Mouse takip değişkenleri
        private System.Windows.Point? _baslangicMousePozisyonu = null;
        private const double HareketeDuyarlilikEsigi = 20;

        // Sistem Boşta Kalma (Idle) Süresi Zamanlayıcısı
        private DispatcherTimer _idleTimer;
        private int _idleSuresiSaniye = Settings.Default.IdleTimeSeconds > 0 ? Settings.Default.IdleTimeSeconds : 5;
        public int IdleSuresiSaniye
        {
            get => _idleSuresiSaniye;
            set => _idleSuresiSaniye = Math.Max(1, value);
        }

        // Web panelinden SSE ile gelen slayt süresi
        private DispatcherTimer _slideTimer = null!;
        private double _slideIntervalSaniye = 5;
        private const int MinSlideIntervalSaniye = 1;
        private const int MaxSlideIntervalSaniye = 300;
        private int _resimAnimasyonSurum;

        // Çevrimdışı (Offline) Önbellek ve Slayt Yönetimi
        private volatile bool _isOffline = true;
        private int _offlineImageIndex = 0;
        private volatile bool _isStreamStopped = false;
        private volatile bool _isInitialSyncCompleted = false;
        private volatile bool _isInitialSyncRunning = false;
        private volatile string? _sonSunucuYayinDosyasi = null;
        private volatile bool _sonSunucuYayinVideoMu = false;
        private volatile string? _sonSunucuYayinBase64 = null;
        private volatile bool _isStreamPinned = false;
        private double _videoStartOffsetSec = 0;

        // System Tray Simgesi
        private Forms.NotifyIcon _notifyIcon = null!;

        // --- Windows Win32 API (Sistem Genelindeki Klavye/Mouse Hareketini Ölçmek İçin) ---
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);


        private bool _bildirimGosterildiMi = false;
        private string _transitionEffect = "fade";
        private string _slideshowOrder = "sequential";
        private bool _showWatermark = false;

        // Mevcut SSE bağlantısını dışarıdan iptal edebilmek için CancellationTokenSource
        private System.Threading.CancellationTokenSource? _sseCancellationTokenSource;

        public MainWindow()
        {
            InitializeComponent();
            UygulaImageFit(Settings.Default.ImageFit);
            UygulaShowInfoBar(Settings.Default.ShowInfoBar);
            UygulaTransitionEffect(Settings.Default.TransitionEffect);
            UygulaSlideshowOrder(Settings.Default.SlideshowOrder);

            // Önbellekteki marka logosunu ve filigran görünürlüğünü başlat
            var cachedLogo = ImageCacheManager.LoadWatermarkBitmap();
            if (cachedLogo != null && WatermarkLogo != null)
            {
                WatermarkLogo.Source = cachedLogo;
            }
            UygulaShowWatermark(Settings.Default.ShowWatermark);

            // Tray Icon Kurulumu
            SistemTepsiSimgesiOlustur();

            this.Loaded += MainWindow_Loaded;
            this.PreviewMouseMove += MainWindow_PreviewMouseMove;

            // Arka planda sistemi kontrol edecek zamanlayıcı (Her 500 ms'de bir kontrol eder)
            _idleTimer = new DispatcherTimer();
            _idleTimer.Interval = TimeSpan.FromMilliseconds(500);
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();

            _slideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _slideTimer.Tick += SlideTimer_Tick;
            // Çevrimdışı moda geçene kadar slayt zamanlayıcısını başlatma (sunucu yayını yönetecek)

            // Saat zamanlayıcısını başlat
            StartSystemClock();
        }

        // Klavyeden herhangi bir tuşa basıldığında uygulamayı tepsiye küçültmek için PreviewKeyDown eventini kullanıyoruz
        
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Yönetici kısayolu: Ctrl + Shift + S doğrudan Ayarlar Penceresini açar
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 &&
                e.Key == System.Windows.Input.Key.S)
            {
                AyarlarPenceresiniAc();
                e.Handled = true;
                return;
            }

            // Herhangi bir klavye tuşuna basıldığında uygulamayı tepsiye küçült
            HideToSystemTray();
            e.Handled = true;
        }

        private void HideToSystemTray()
        {
            this.Hide();
            Logger.LogInfo("Kullanıcı klavye etkileşimi nedeniyle uygulama tepsiye küçültüldü.");
        }

        private void StartSystemClock()
        {
            // Anlık olarak saati bas
            UpdateClockText();

            // Her 1 saniyede bir tetiklenecek zamanlayıcı
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _clockTimer.Tick += (sender, e) => UpdateClockText();
            _clockTimer.Start();
        }

        private void UpdateClockText()
        {
            // Sistem dilinden bağımsız olarak Türkçe uzun tarih ve saat formatı kullanıyoruz
            var culture = new CultureInfo("tr-TR");

            // Çıktı Örneği: 19 Eylül 2026, Cumartesi — 14:35:08
            string tarihSaatMetni = DateTime.Now.ToString("dd MMMM yyyy, dddd  HH:mm:ss", culture);

            TxtSystemClock.Text = tarihSaatMetni;
        }

        // System Tray (Sağ Alt Simge) Yapılandırması
        private void SistemTepsiSimgesiOlustur()
        {
            _notifyIcon = new Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application; // Varsayılan sistem simgesi
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "IdleBoard";

            // Sağ Tık Menüsü
            var contextMenu = new Forms.ContextMenuStrip();

            var itemGoster = new Forms.ToolStripMenuItem("Tam Ekran Göster");
            itemGoster.Click += (s, e) => UygulamayiAc();

            // AYARLAR MENÜSÜ
            var itemAyarlar = new Forms.ToolStripMenuItem("Ayarlar");
            itemAyarlar.Click += (s, e) => AyarlarPenceresiniAc();

            var itemCikis = new Forms.ToolStripMenuItem("Çıkış");
            itemCikis.Click += (s, e) => UygulamayiTamamenKapat();

            contextMenu.Items.Add(itemGoster);
            contextMenu.Items.Add(itemAyarlar);
            contextMenu.Items.Add(new Forms.ToolStripSeparator());
            contextMenu.Items.Add(itemCikis);

            _notifyIcon.ContextMenuStrip = contextMenu;

            // Simgeye çift tıklandığında da açılması için
            _notifyIcon.DoubleClick += (s, e) => UygulamayiAc();
        }

        private void AyarlarPenceresiniAc()
        {
            var settingsWin = new SettingsWindow(ServerUrl);

            if (settingsWin.ShowDialog() == true)
            {
                // 1. Yeni adresi değişkene ata
                bool urlDegisti = ServerUrl != settingsWin.ServerUrl;
                ServerUrl = settingsWin.ServerUrl;

                // 2. Ayarları kalıcı olarak kaydet (Disk üzerindeki user.config dosyasına yazılır)
                Settings.Default.ServerUrl = ServerUrl;
                Settings.Default.Save();

                // 3. Eğer URL değiştiyse yeni bağlantıyı başlat
                if (urlDegisti)
                {
                    _isInitialSyncCompleted = false;
                    LoadingOverlay.Visibility = System.Windows.Visibility.Visible;
                    TxtLoadingTitle.Text = "Veriler alınıyor...";
                    TxtLoadingStatus.Text = "Yeni sunucu adresi ile eşitleniyor...";
                    SseBaslat();
                    BaslatBaglantiZamanAsimiTakibi();
                }

                System.Windows.MessageBox.Show("Ayarlar kaydedildi.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

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

        // Arka planda çalışarak kullanıcı belirlenen boşta kalma süresi boyunca inaktif mi diye kontrol eden metod
        private void IdleTimer_Tick(object? sender, EventArgs e)
        {
            // Eğer sunucudan yayını durdur komutu geldiyse sistem boşta kalsa bile uygulamayı açma
            if (_isStreamStopped) return;

            uint idleSuresiMs = GetIdleTimeMs();

            // Eğer kullanıcı belirlenen süreden fazla süredir mouse/klavye kullanmadıysa ve uygulama gizliyse:
            if (idleSuresiMs >= ((uint)IdleSuresiSaniye * 1000) && !this.IsVisible)
            {
                UygulamayiAc();
            }
        }

        // Windows'un son girdi süresini milisaniye cinsinden döndürür
        private static uint GetIdleTimeMs()
        {
            LASTINPUTINFO lastInputInfo = new LASTINPUTINFO();
            lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

            if (GetLastInputInfo(ref lastInputInfo))
            {
                return (uint)Environment.TickCount - lastInputInfo.dwTime;
            }
            return 0;
        }

        private void MainWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Sadece uygulama ekranda görünürken mouse hareketlerini algıla
            if (!this.IsVisible) return;

            System.Windows.Point mevcutPozisyon = e.GetPosition(this);

            if (!_baslangicMousePozisyonu.HasValue)
            {
                _baslangicMousePozisyonu = mevcutPozisyon;
                return;
            }

            double xFarki = Math.Abs(mevcutPozisyon.X - _baslangicMousePozisyonu.Value.X);
            double yFarki = Math.Abs(mevcutPozisyon.Y - _baslangicMousePozisyonu.Value.Y);

            // Mouse hareket ettiğinde uygulamayı kapatmak yerine GİZLİYORUZ
            if (xFarki > HareketeDuyarlilikEsigi || yFarki > HareketeDuyarlilikEsigi)
            {
                UygulamayiGizle();
            }
        }

        private void UygulamayiGizle()
        {
            _baslangicMousePozisyonu = null;
            this.Hide();

            // Bildirim daha önce gösterilmediyse sadece ilk gizlenmede göster
            if (!_bildirimGosterildiMi)
            {
                _notifyIcon.ShowBalloonTip(1500, "IdleBoard", "Uygulama tepside çalışmaya devam ediyor.", Forms.ToolTipIcon.Info);
                _bildirimGosterildiMi = true; // Bayrağı güncelle, bir daha çalışmasın
            }
        }

        private void UygulamayiAc()
        {
            this.Show();
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.Activate(); // Öne getir
            _baslangicMousePozisyonu = null; // Yeni açılış için pozisyonu sıfırla
        }

        private void UygulamayiTamamenKapat()
        {
            // Tray Icon'u bellekten temizle ve kapat
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
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

        private void SlideTimer_Tick(object? sender, EventArgs e)
        {
            // Sabitlenmiş veya durdurulmuş yayında ya da çevrimiçi modda asla yerel slaytı ilerletme
            if (_isStreamPinned || _isStreamStopped || !_isOffline)
            {
                return;
            }

            // Çevrimdışı modda slayt döngüsünü yerel zamanlayıcı yönetir
            SonrakiOfflineGorseliniGoster();
        }

        private void SetStreamStopped(bool stopped)
        {
            if (_isStreamStopped == stopped) return;
            _isStreamStopped = stopped;

            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (stopped)
                {
                    Logger.LogInfo("Sunucudan yayını durdur komutu alındı. İstemci sistem tepsisine gizlendi.");
                    this.Hide();
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Text = "IdleBoard: Yayın Durduruldu";
                    }
                    if (TxtStatus != null)
                    {
                        TxtStatus.Text = "Yayın Durduruldu (Tepside)";
                    }
                    if (DotStatus != null)
                    {
                        DotStatus.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // Turuncu
                    }
                }
                else
                {
                    Logger.LogInfo("Sunucudan yayını başlat komutu alındı. İstemci ekrana getirildi.");
                    UygulamayiAc();
                    DurumIconGuncelle(BaglantiDurumu.Bagli);
                }
            }));
        }

        private void ClearLiveImage()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                ++_resimAnimasyonSurum;
                if (mediaVideo.Visibility == Visibility.Visible)
                {
                    mediaVideo.Stop();
                    mediaVideo.Source = null;
                    mediaVideo.Visibility = Visibility.Collapsed;
                }
                imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                imgCanli.Source = null;
                imgCanli.Opacity = 1.0;
                imgCanli.Visibility = Visibility.Visible;
                if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Visible;
            }));
        }

        private void SetOfflineMode(bool offline)
        {
            if (_isOffline == offline) return;
            _isOffline = offline;

            if (_isOffline)
            {
                Logger.LogInfo("Ağ/Sunucu bağlantısı koptu. Çevrimdışı (offline) önbellek moduna geçildi.");
                EkranaYazdir("[ÇEVRİMDISI MOD] Önbellekteki görsellerle slayt gösterisi devam ediyor.");

                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (!_isStreamPinned && !_isStreamStopped && !_slideTimer.IsEnabled)
                    {
                        _slideTimer.Start();
                    }

                    if (imgCanli.Source == null && ImageCacheManager.GetCachedCount() > 0)
                    {
                        SonrakiOfflineGorseliniGoster();
                    }
                }));
            }
            else
            {
                Logger.LogInfo("Sunucu bağlantısı yeniden sağlandı. Çevrimiçi canlı yayın devralındı.");
                EkranaYazdir("[ÇEVRİMİÇİ MOD] Sunucu yayını aktif.");

                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (_slideTimer.IsEnabled)
                    {
                        _slideTimer.Stop();
                    }
                }));
            }
        }

        private void SonrakiOfflineGorseliniGoster()
        {
            var cachedFiles = ImageCacheManager.GetCachedImageFiles();
            if (cachedFiles.Count == 0)
            {
                EkranaYazdir("[ÇEVRİMDISI] Önbellekte gösterilecek kayıtlı görsel bulunamadı.");
                return;
            }

            if (_slideshowOrder == "random" && cachedFiles.Count > 1)
            {
                int nextIdx;
                do
                {
                    nextIdx = Random.Shared.Next(cachedFiles.Count);
                } while (nextIdx == _offlineImageIndex);
                _offlineImageIndex = nextIdx;
            }
            else
            {
                if (_offlineImageIndex >= cachedFiles.Count)
                {
                    _offlineImageIndex = 0;
                }
            }

            string filePath = cachedFiles[_offlineImageIndex];
            if (_slideshowOrder != "random")
            {
                _offlineImageIndex = (_offlineImageIndex + 1) % cachedFiles.Count;
            }

            if (ImageCacheManager.IsVideoFile(filePath))
            {
                Logger.LogInfo($"[Çevrimdışı Slayt] Video gösteriliyor: {System.IO.Path.GetFileName(filePath)} ({_offlineImageIndex + 1}/{cachedFiles.Count})");
                VideoyuGuncelle(System.IO.Path.GetFileName(filePath));
                if (_slideTimer.IsEnabled)
                {
                    _slideTimer.Stop();
                    _slideTimer.Interval = TimeSpan.FromSeconds(65);
                    _slideTimer.Start();
                }
            }
            else
            {
                if (_slideTimer.Interval != TimeSpan.FromSeconds(_slideIntervalSaniye))
                {
                    _slideTimer.Interval = TimeSpan.FromSeconds(_slideIntervalSaniye);
                }
                var bitmap = ImageHelper.LoadBitmapFromFile(filePath);
                if (bitmap != null)
                {
                    Logger.LogInfo($"[Çevrimdışı Slayt] Görsel gösteriliyor: {System.IO.Path.GetFileName(filePath)} ({_offlineImageIndex + 1}/{cachedFiles.Count})");
                    ResmiGuncelle(bitmap);
                }
            }
        }

        private void UygulaSlideInterval(double saniye)
        {
            if (saniye < MinSlideIntervalSaniye || saniye > MaxSlideIntervalSaniye)
                return;

            double yuvarlak = Math.Round(saniye);
            _slideIntervalSaniye = yuvarlak;
            var yeniInterval = TimeSpan.FromSeconds(yuvarlak);

            void Uygula()
            {
                if (_slideTimer.Interval == yeniInterval)
                    return;

                _slideTimer.Interval = yeniInterval;
                if (_isOffline && !_isStreamPinned && !_isStreamStopped)
                {
                    _slideTimer.Stop();
                    _slideTimer.Start();
                }
                EkranaYazdir($"Slayt süresi güncellendi: {yeniInterval.TotalSeconds:0}sn");
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaBostaKalmaZamanAsimi(int saniye)
        {
            if (saniye < 1 || saniye > 86400)
                return;

            if (IdleSuresiSaniye == saniye)
                return;

            IdleSuresiSaniye = saniye;
            Settings.Default.IdleTimeSeconds = saniye;
            Settings.Default.Save();

            Logger.LogInfo($"Sunucudan boşta kalma süresi güncellendi: {saniye} saniye");
            EkranaYazdir($"Boşta kalma süresi güncellendi: {saniye}sn");
        }

        private void UygulaImageFit(string? fitMode)
        {
            if (string.IsNullOrWhiteSpace(fitMode))
                return;

            Stretch stretch;
            switch (fitMode.Trim().ToLowerInvariant())
            {
                case "fill":
                    stretch = Stretch.Fill;
                    break;
                case "uniformtofill":
                case "cover":
                    stretch = Stretch.UniformToFill;
                    break;
                case "none":
                    stretch = Stretch.None;
                    break;
                case "uniform":
                case "contain":
                default:
                    stretch = Stretch.Uniform;
                    break;
            }

            void Uygula()
            {
                if (imgCanli.Stretch != stretch || mediaVideo.Stretch != stretch)
                {
                    imgCanli.Stretch = stretch;
                    mediaVideo.Stretch = stretch;
                    Settings.Default.ImageFit = stretch.ToString();
                    Settings.Default.Save();

                    Logger.LogInfo($"Görsel ve video yerleşimi güncellendi: {stretch}");
                    EkranaYazdir($"Görsel yerleşimi: {stretch}");
                }
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaShowInfoBar(bool visible)
        {
            void Uygula()
            {
                var targetVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (BottomBanner.Visibility != targetVisibility)
                {
                    BottomBanner.Visibility = targetVisibility;
                    Settings.Default.ShowInfoBar = visible;
                    Settings.Default.Save();

                    Logger.LogInfo($"Alt bilgi şeridi güncellendi: {(visible ? "Göster" : "Gizle")}");
                    EkranaYazdir($"Alt bilgi şeridi: {(visible ? "Göster" : "Gizle")}");
                }
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private Image? WatermarkLogo => (Image?)FindName("ImgWatermark");

        private void UygulaWatermarkImage(string? base64)
        {
            void Uygula()
            {
                var logo = WatermarkLogo;
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    var bmp = ImageHelper.Base64ToBitmapImage(base64, null);
                    if (bmp != null)
                    {
                        if (logo != null)
                        {
                            logo.Source = bmp;
                        }
                        _ = ImageCacheManager.SaveWatermarkImageAsync(base64);
                        Logger.LogInfo("Marka logosu güncellendi.");
                    }
                }
                else
                {
                    if (logo != null)
                    {
                        logo.Source = null;
                    }
                    ImageCacheManager.DeleteWatermarkImage();
                    Logger.LogInfo("Marka logosu temizlendi.");
                }
                GuncelleWatermarkGorunurluk();
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaShowWatermark(bool visible)
        {
            void Uygula()
            {
                _showWatermark = visible;
                Settings.Default.ShowWatermark = visible;
                Settings.Default.Save();
                GuncelleWatermarkGorunurluk();
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void GuncelleWatermarkGorunurluk()
        {
            var logo = WatermarkLogo;
            bool gorunur = _showWatermark && logo != null && logo.Source != null;
            var targetPanel = WatermarkPanel ?? (Border?)FindName("WatermarkPanel");
            if (targetPanel != null)
            {
                var hedef = gorunur ? Visibility.Visible : Visibility.Collapsed;
                if (targetPanel.Visibility != hedef)
                {
                    targetPanel.Visibility = hedef;
                    Logger.LogInfo($"Marka logosu görünürlüğü güncellendi: {(gorunur ? "Göster" : "Gizle")}");
                    EkranaYazdir($"Filigran: {(gorunur ? "Göster" : "Gizle")}");
                }
            }
        }

        private void UygulaTransitionEffect(string? effect)
        {
            if (string.IsNullOrWhiteSpace(effect))
                return;

            string normalized = (effect.Trim().ToLowerInvariant() == "none") ? "none" : "fade";
            if (_transitionEffect == normalized)
                return;

            _transitionEffect = normalized;
            Settings.Default.TransitionEffect = normalized;
            Settings.Default.Save();

            Logger.LogInfo($"Geçiş efekti güncellendi: {normalized}");
            EkranaYazdir($"Geçiş efekti: {normalized}");
        }

        private void UygulaSlideshowOrder(string? order)
        {
            if (string.IsNullOrWhiteSpace(order))
                return;

            string normalized = (order.Trim().ToLowerInvariant() == "random") ? "random" : "sequential";
            if (_slideshowOrder == normalized)
                return;

            _slideshowOrder = normalized;
            Settings.Default.SlideshowOrder = normalized;
            Settings.Default.Save();

            Logger.LogInfo($"Slayt sıralaması güncellendi: {normalized}");
            EkranaYazdir($"Slayt sıralaması: {normalized}");
        }

        private TimeSpan FadeSuresiAl()
        {
            double yariMs = _slideIntervalSaniye * 1000 * 0.2;
            double ms = Math.Clamp(yariMs, 120, 700);
            return TimeSpan.FromMilliseconds(ms);
        }

        private int _videoPlayVersion = 0;

        private async void VideoyuGuncelle(string dosyaAdi, double startOffsetSec = 0)
        {
            try
            {
                int version = ++_videoPlayVersion;
                _videoStartOffsetSec = Math.Max(0, startOffsetSec);

                // 1. Videonun yerel diskte olduğundan emin ol (Kesinlikle canlı ağdan akış yapılmaz!)
                string? localPath = await ImageCacheManager.EnsureFileDownloadedAsync(ServerUrl, dosyaAdi);

                // Eğer beklerken başka bir video veya slayta geçildiyse işlemi durdur
                if (version != _videoPlayVersion) return;

                if (string.IsNullOrEmpty(localPath) || !System.IO.File.Exists(localPath))
                {
                    Logger.LogError($"[Video Oynatıcı] Video yerel diske indirilemedi: {dosyaAdi}. Siyah ekran verilmiyor, mevcut görsel korunuyor.");
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (version != _videoPlayVersion) return;

                    var videoUri = new Uri(localPath, UriKind.Absolute);

                    if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Collapsed;
                    mediaVideo.Stop();
                    mediaVideo.Source = videoUri;
                    mediaVideo.Volume = 0;
                    mediaVideo.IsMuted = true;
                    mediaVideo.Play();

                    Logger.LogInfo($"[Video Oynatıcı] Video yerel diskten oynatılıyor: {dosyaAdi}");
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Video Oynatıcı] Video yerel diskten başlatılırken hata: {dosyaAdi}", ex);
            }
        }

        private void MediaVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    mediaVideo.Visibility = Visibility.Visible;
                    mediaVideo.Opacity = 1.0;

                    if (_videoStartOffsetSec > 0.5)
                    {
                        mediaVideo.Position = TimeSpan.FromSeconds(_videoStartOffsetSec);
                        _videoStartOffsetSec = 0;
                    }

                    mediaVideo.Play();

                    // Video açılıp ilk kare hazır olunca önceki görseli temizle (artık bellekte veya ekranda eski resim kalmaz)
                    imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                    imgCanli.Opacity = 0.0;
                    imgCanli.Source = null;
                    imgCanli.Visibility = Visibility.Collapsed;
                }
                catch { }
            }));
        }

        private void MediaVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isStreamPinned)
                {
                    // Sabitlenmiş videoyu baştan döngüye al
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        mediaVideo.Position = TimeSpan.Zero;
                        mediaVideo.Play();
                    }));
                    return;
                }

                mediaVideo.Pause();
                if (_isOffline)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        SonrakiOfflineGorseliniGoster();
                    }));
                }
            }
            catch { }
        }

        private void MediaVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            Logger.LogError($"[Video Oynatıcı] Video yerel diskten oynatılamadı: {e.ErrorException?.Message}", e.ErrorException);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                mediaVideo.Visibility = Visibility.Collapsed;
                mediaVideo.Source = null;
                // Asla siyah ekranda bırakma, yerel diskteki bir sonraki görseli göster
                SonrakiOfflineGorseliniGoster();
            }));
        }

        private void ResmiGuncelle(string? base64String)
        {
            if (string.IsNullOrWhiteSpace(base64String)) return;

            var yeniBitmap = ImageHelper.Base64ToBitmapImage(base64String);
            if (yeniBitmap != null)
            {
                ResmiGuncelle(yeniBitmap);
            }
        }

        private void ResmiGuncelle(BitmapImage yeniBitmap)
        {
            // Yüksek frekanslı resim değişimlerinde UI'ı kilitlememek için BeginInvoke kullanıyoruz
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    int surum = ++_resimAnimasyonSurum;
                    TimeSpan fadeSuresi = FadeSuresiAl();
                    TimeSpan doubleFade = TimeSpan.FromMilliseconds(fadeSuresi.TotalMilliseconds * 2);

                    // Her zaman görsel ekranını görünür kıl ve bekleme panelini gizle
                    imgCanli.Visibility = Visibility.Visible;
                    if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Collapsed;

                    // EĞER VİDEODAN RESME GEÇİLİYORSA:
                    // Eski resmi hiç devreye sokmadan doğrudan yeni resmi videonun dondurulmuş son karesi üstüne açıyoruz!
                    if (mediaVideo.Visibility == Visibility.Visible)
                    {
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Visibility = Visibility.Visible;

                        if (_transitionEffect == "none")
                        {
                            imgCanli.Opacity = 1.0;
                            mediaVideo.Stop();
                            mediaVideo.Source = null;
                            mediaVideo.Visibility = Visibility.Collapsed;
                            return;
                        }

                        imgCanli.Opacity = 0.0;
                        var videoFadeIn = new DoubleAnimation(0.0, 1.0, doubleFade)
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        videoFadeIn.Completed += (s, e) =>
                        {
                            if (surum != _resimAnimasyonSurum) return;
                            try
                            {
                                mediaVideo.Stop();
                                mediaVideo.Source = null;
                                mediaVideo.Visibility = Visibility.Collapsed;
                            }
                            catch { }
                        };
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, videoFadeIn);
                        return;
                    }

                    // NORMAL RESİMDEN RESME GEÇİŞ VEYA İLK AÇILIŞ
                    if (_transitionEffect == "none" || imgCanli.Source == null)
                    {
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Opacity = 1.0;
                        return;
                    }

                    // Ensure no lingering animations and start from fully visible
                    imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                    imgCanli.Opacity = 1.0;

                    // Fade-out from fully visible to fully transparent (double duration)
                    var fadeOut = new DoubleAnimation(1.0, 0.0, doubleFade)
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                    };

                    fadeOut.Completed += (s, e) =>
                    {
                        if (surum != _resimAnimasyonSurum)
                            return;

                        // Swap image source while fully transparent
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Opacity = 0.0;

                        // Fade-in the new image
                        var fadeIn = new DoubleAnimation(0.0, 1.0, doubleFade)
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };

                        imgCanli.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    };

                    imgCanli.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                catch (Exception ex)
                {
                    EkranaYazdir($"[RESİM GÜNCELLEME HATASI] {ex.Message}");
                }
            }));
        }

        private void EkranaYazdir(string mesaj)
        {
            // Arayüzde log alanı olmadığı için metinleri Visual Studio Output penceresine yazdırıyoruz
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {mesaj}");
        }

        public enum BaglantiDurumu
        {
            Bagli,      // Yeşil
            Baglaniyor, // Sarı / Turuncu
            Koptu       // Kırmızı
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        // 16x16 piksel boyutunda dinamik renkli daire ikon oluşturur
        private System.Drawing.Icon OlusturDurumIkonu(System.Drawing.Color renk)
        {
            using var bitmap = new System.Drawing.Bitmap(16, 16);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var brush = new System.Drawing.SolidBrush(renk);
                g.FillEllipse(brush, 2, 2, 12, 12);
            }

            IntPtr hIcon = bitmap.GetHicon();
            // Handle'dan ikonu oluşturup Clone ile tam bağımsız nesne haline getiriyoruz
            using var tempIcon = System.Drawing.Icon.FromHandle(hIcon);
            var finalIcon = (System.Drawing.Icon)tempIcon.Clone();

            // GDI sızıntısını engellemek için handle klonlandıktan sonra serbest bırakılır
            DestroyIcon(hIcon); 

            return finalIcon;
        }

        private void DurumIconGuncelle(BaglantiDurumu durum, string mesaj = "")
        {
            // Arka plan thread'lerinden çağrıldığında UI thread'ine yönlendiriyoruz
            Dispatcher.Invoke(() =>
            {
                int cacheCount = ImageCacheManager.GetCachedCount();

                if (_notifyIcon != null)
                {
                    switch (durum)
                    {
                        case BaglantiDurumu.Bagli:
                            _notifyIcon.Icon = OlusturDurumIkonu(System.Drawing.Color.Green);
                            _notifyIcon.Text = $"IdleBoard: Bağlantı Aktif\n{ServerUrl}";
                            break;

                        case BaglantiDurumu.Baglaniyor:
                            _notifyIcon.Icon = OlusturDurumIkonu(System.Drawing.Color.Orange);
                            _notifyIcon.Text = string.IsNullOrWhiteSpace(mesaj)
                                ? "IdleBoard: Bağlanıyor..."
                                : $"IdleBoard: Bağlanıyor...\n{mesaj}";
                            break;

                        case BaglantiDurumu.Koptu:
                            _notifyIcon.Icon = OlusturDurumIkonu(System.Drawing.Color.Red);
                            // NotifyIcon.Text has a strict max length; show only a short status here
                            _notifyIcon.Text = cacheCount > 0
                                ? $"IdleBoard: Çevrimdışı ({cacheCount} Önbellek)"
                                : "IdleBoard: Bağlantı Kesildi!";
                            // Log the full message to application logs (avoid putting long text in the tray icon)
                            if (!string.IsNullOrWhiteSpace(mesaj))
                            {
                                Logger.LogError($"Bağlantı koptu: {mesaj}");
                            }
                            else
                            {
                                Logger.LogError("Bağlantı koptu.");
                            }
                            break;
                    }
                }

                // Alt bilgi şeridindeki durum göstergesini senkronize et
                if (DotStatus != null && TxtStatus != null)
                {
                    switch (durum)
                    {
                        case BaglantiDurumu.Bagli:
                            DotStatus.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // Yeşil
                            TxtStatus.Text = "Canlı Yayın";
                            TxtStatus.Foreground = System.Windows.Media.Brushes.White;
                            break;

                        case BaglantiDurumu.Baglaniyor:
                            DotStatus.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // Turuncu
                            TxtStatus.Text = string.IsNullOrWhiteSpace(mesaj) ? "Bağlanıyor..." : mesaj;
                            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));
                            break;

                        case BaglantiDurumu.Koptu:
                            DotStatus.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // Kırmızı
                            // Show a short, clear status when connection is lost.
                            // Continue relying on the existing slideshow loop to keep showing cached images.
                            TxtStatus.Text = cacheCount > 0
                                ? $"Çevrimdışı ({cacheCount} Önbellek)"
                                : "Bağlantı koptu (Önbellek Yok)";
                            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(252, 165, 165));
                            break;
                    }
                }
            });
        }
    }
}