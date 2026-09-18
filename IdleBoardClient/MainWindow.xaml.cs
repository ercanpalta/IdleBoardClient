using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using IdleBoardClient;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Animation;

// Forms ve WPF Isim Çakışmasını Önlemek İçin Alias
using Forms = System.Windows.Forms;

namespace SseWpfApp
{
    public partial class MainWindow : Window
    {
        // Varsayılan adresi kaydedilen Settings ayarlarından çekiyoruz
        private string ServerUrl = Settings.Default.ServerUrl;

        // Mouse takip değişkenleri
        private System.Windows.Point? _baslangicMousePozisyonu = null;
        private const double HareketeDuyarlilikEsigi = 20;

        // Sistem Boşta Kalma (Idle) Süresi Zamanlayıcısı
        private DispatcherTimer _idleTimer;
        private const int IdleSuresiSaniye = 3; // 3 saniye boyunca işlem yapılmazsa açılacak

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

        // Mevcut SSE bağlantısını dışarıdan iptal edebilmek için CancellationTokenSource
        private System.Threading.CancellationTokenSource? _sseCancellationTokenSource;

        public MainWindow()
        {
            InitializeComponent();

            // Tray Icon Kurulumu
            SistemTepsiSimgesiOlustur();

            this.Loaded += MainWindow_Loaded;
            this.PreviewMouseMove += MainWindow_PreviewMouseMove;

            // Arka planda sistemi kontrol edecek zamanlayıcı (Her 500 ms'de bir kontrol eder)
            _idleTimer = new DispatcherTimer();
            _idleTimer.Interval = TimeSpan.FromMilliseconds(500);
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();
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
                ServerUrl = settingsWin.ServerUrl;

                // 2. Ayarı kalıcı olarak kaydet (Disk üzerindeki user.config dosyasına yazılır)
                Settings.Default.ServerUrl = ServerUrl;
                Settings.Default.Save();

                // 3. Yeni bağlantıyı başlat
                SseBaslat();

                System.Windows.MessageBox.Show("Sunucu adresi kaydedildi ve yeni adrese bağlanılıyor...", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Eğer kaydedilmiş bir URL yoksa (ilk açılış veya boş bırakılmışsa)
            if (string.IsNullOrWhiteSpace(ServerUrl))
            {
                bool kayitBaşarili = IlkAcalistaUrlSor();

                // Kullanıcı iptal ederse veya boş geçerse uygulamayı açma
                if (!kayitBaşarili)
                {
                    System.Windows.MessageBox.Show("Sunucu adresi belirtilmediği için uygulama kapatılıyor.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                    UygulamayiTamamenKapat();
                    return;
                }
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

        // Arka planda çalışarak kullanıcı 3 saniyedir inaktif mi diye kontrol eden metod
        private void IdleTimer_Tick(object? sender, EventArgs e)
        {
            uint idleSuresiMs = GetIdleTimeMs();

            // Eğer kullanıcı 3 saniyeden fazla süredir mouse/klavye kullanmadıysa ve uygulama gizliyse:
            if (idleSuresiMs >= (IdleSuresiSaniye * 1000) && !this.IsVisible)
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

                    // Başarılı bağlantı kurulduğunda bekleme süresini varsayılana sıfırla
                    retryDelaySeconds = 2;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        if (line == null) // end of stream
                            break;

                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        // Event Stream standardına uygun "data: " satırlarını işle
                        if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                        {
                            string jsonString = line.Substring(6).Trim();

                            try
                            {
                                using var doc = JsonDocument.Parse(jsonString);
                                var root = doc.RootElement;

                                if (root.TryGetProperty("resimData", out var resimElement))
                                {
                                    string base64Image = resimElement.GetString() ?? string.Empty;
                                    if (!string.IsNullOrEmpty(base64Image))
                                    {
                                        ResmiGuncelle(base64Image);
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
                    EkranaYazdir($"[AĞ HATASI] Sunucuya ulaşılamıyor: {ex.Message}");
                }
                catch (Exception ex)
                {
                    EkranaYazdir($"[BEKLENMEYEN HATA] {ex.Message}");
                }

                // İptal edilmediyse ve akış koptuysa katlanarak bekle ve tekrar dene
                if (!cancellationToken.IsCancellationRequested)
                {
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

        private void ResmiGuncelle(string? base64String)
        {
            // Yüksek frekanslı resim değişimlerinde UI'ı kilitlememek için BeginInvoke kullanıyoruz
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    // 1. ImageHelper kullanarak bellek dostu, dondurulmuş (Frozen) resmi oluşturuyoruz
                    BitmapImage? yeniBitmap = ImageHelper.Base64ToBitmapImage(base64String);

                    if (yeniBitmap == null) return;

                    // 2. FADE-OUT (Kararma) Animasyonu
                    var fadeOut = new DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.0,
                        Duration = TimeSpan.FromMilliseconds(700)
                    };

                    // Görsel karardığında yeni resmi yükleyip eski resmi bellekten düşürüyoruz
                    fadeOut.Completed += (s, e) =>
                    {
                        // Eski resmi bellekten temizlemek için referansı sıfırlıyoruz
                        if (imgCanli.Source is BitmapImage)
                        {
                            imgCanli.Source = null;
                        }

                        // Yeni dondurulmuş resmi atıyoruz
                        imgCanli.Source = yeniBitmap;

                        // 3. FADE-IN (Açılma) Animasyonu
                        var fadeIn = new DoubleAnimation
                        {
                            From = 0.0,
                            To = 1.0,
                            Duration = TimeSpan.FromMilliseconds(700)
                        };

                        imgCanli.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);
                    };

                    // Animasyonu başlat
                    imgCanli.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeOut);
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
    }
}