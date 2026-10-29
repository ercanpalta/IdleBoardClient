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
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.Default;
                int tier = RenderCapability.Tier >> 16;
                Logger.LogInfo($"[Grafik Donanımı] WPF Render Seviyesi: Tier {tier} (2 = Tam Donanım Hızlandırma)");
            }
            catch (Exception ex)
            {
                Logger.LogError("[Grafik Donanımı] Render modu ayarlanırken hata:", ex);
            }
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
    }
}
