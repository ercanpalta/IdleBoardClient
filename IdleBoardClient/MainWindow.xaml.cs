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
        private volatile bool _isOffline = false;
        private int _offlineImageIndex = 0;
        private volatile bool _isStreamStopped = false;

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
            _slideTimer.Start();

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
                    SseBaslat();
                }

                System.Windows.MessageBox.Show("Ayarlar kaydedildi.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Eğer kaydedilmiş bir URL yoksa varsayılan yerel adresi kullan
            if (string.IsNullOrWhiteSpace(ServerUrl))
            {
                ServerUrl = "http://localhost:3000/events";
                Settings.Default.ServerUrl = ServerUrl;
                Settings.Default.Save();
            }

            // Önbellekte görsel varsa ilk açılışta siyah ekran kalmaması için göster
            if (ImageCacheManager.GetCachedCount() > 0)
            {
                SonrakiOfflineGorseliniGoster();
            }

            // Geçerli URL varsa bağlantıyı başlat
            SseBaslat();
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

                    using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl);
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
                        TimeSpan streamReadTimeout = TimeSpan.FromSeconds(Math.Max(20, _slideIntervalSaniye * 2.5));
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

                                if (root.TryGetProperty("tip", out var tipElement))
                                {
                                    string tip = tipElement.GetString() ?? "";
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
                                    }
                                    else if (tip == "init")
                                    {
                                        if (root.TryGetProperty("isPaused", out var isPausedElement) && isPausedElement.GetBoolean())
                                        {
                                            SetStreamStopped(true);
                                        }
                                    }
                                }

                                if (root.TryGetProperty("slideInterval", out var intervalElement)
                                    && intervalElement.ValueKind == JsonValueKind.Number)
                                {
                                    UygulaSlideInterval(intervalElement.GetDouble());
                                }

                                if (root.TryGetProperty("kioskIdleTimeout", out var idleElement)
                                    && idleElement.ValueKind == JsonValueKind.Number)
                                {
                                    UygulaKioskIdleTimeout(idleElement.GetInt32());
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
                                    _ = ImageCacheManager.SyncActiveImagesAsync(ServerUrl, activeList, cancellationToken);
                                }

                                if (root.TryGetProperty("resimData", out var resimElement))
                                {
                                    string base64Image = resimElement.GetString() ?? string.Empty;
                                    string? dosyaAdi = null;
                                    if (root.TryGetProperty("dosyaAdi", out var dosyaElement))
                                    {
                                        dosyaAdi = dosyaElement.GetString();
                                    }

                                    if (!string.IsNullOrEmpty(base64Image))
                                    {
                                        ResmiGuncelle(base64Image);

                                        // Alınan görseli arka planda diske önbelleğe kaydet
                                        _ = ImageCacheManager.SaveImageAsync(dosyaAdi, base64Image);
                                    }
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
            // Çevrimdışı modda slayt döngüsünü yerel zamanlayıcı yönetir
            if (_isOffline)
            {
                SonrakiOfflineGorseliniGoster();
            }
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
                imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                imgCanli.Source = null;
                imgCanli.Opacity = 1.0;
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
                    if (!_slideTimer.IsEnabled)
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

            var bitmap = ImageHelper.LoadBitmapFromFile(filePath);
            if (bitmap != null)
            {
                Logger.LogInfo($"[Çevrimdışı Slayt] Görsel gösteriliyor: {System.IO.Path.GetFileName(filePath)} ({_offlineImageIndex + 1}/{cachedFiles.Count})");
                ResmiGuncelle(bitmap);
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

                _slideTimer.Stop();
                _slideTimer.Interval = yeniInterval;
                _slideTimer.Start();
                EkranaYazdir($"Slayt süresi güncellendi: {yeniInterval.TotalSeconds:0}sn");
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaKioskIdleTimeout(int saniye)
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
                if (imgCanli.Stretch != stretch)
                {
                    imgCanli.Stretch = stretch;
                    Settings.Default.ImageFit = stretch.ToString();
                    Settings.Default.Save();

                    Logger.LogInfo($"Görsel yerleşimi güncellendi: {stretch}");
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
                    if (_transitionEffect == "none")
                    {
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                        imgCanli.Opacity = 1.0;
                        imgCanli.Source = yeniBitmap;
                        return;
                    }

                    int surum = ++_resimAnimasyonSurum;
                    TimeSpan fadeSuresi = FadeSuresiAl();
                    // Double the fade duration as requested
                    TimeSpan doubleFade = TimeSpan.FromMilliseconds(fadeSuresi.TotalMilliseconds * 2);

                    // Ensure no lingering animations and start from fully visible
                    imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                    imgCanli.Opacity = 1.0;

                    if (imgCanli.Source == null)
                    {
                        // No current image: directly show new image with fade-in
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Opacity = 0.0;
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, doubleFade)
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        });
                        return;
                    }

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