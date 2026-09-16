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
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromMilliseconds(-1);

                    using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl);
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);

                    EkranaYazdir($"[{ServerUrl}] adresine bağlantı aktif. Resimler alınıyor...");

                    // Read lines asynchronously until the stream ends or cancellation is requested
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        if (line == null) // end of stream
                            break;

                        if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("data: "))
                        {
                            string jsonString = line.Substring(6);

                            using var doc = JsonDocument.Parse(jsonString);
                            var root = doc.RootElement;

                            string? zaman = root.GetProperty("zaman").GetString();
                            string? mesaj = root.GetProperty("mesaj").GetString();
                            string? base64Image = root.GetProperty("resimData").GetString();

                            if (string.IsNullOrEmpty(base64Image))
                            {
                                EkranaYazdir("[RESİM HATASI] resimData is null or empty");
                                continue;
                            }

                            ResmiGuncelle(base64Image);
                            EkranaYazdir($"{zaman ?? "?"} - {mesaj ?? string.Empty}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // URL değiştiğinde veya bağlantı kasıtlı iptal edildiğinde buraya düşer
                    EkranaYazdir("Eski SSE bağlantısı kapatıldı.");
                    break; // Döngüden temiz bir şekilde çık
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    EkranaYazdir($"[HATA] {ex.Message}. 5s sonra tekrar deneniyor...");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private void ResmiGuncelle(string? base64String)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(base64String))
                    {
                        EkranaYazdir("[RESİM HATASI] resimData is null or empty");
                        return;
                    }

                    byte[] imageBytes = Convert.FromBase64String(base64String);
                    using var ms = new MemoryStream(imageBytes);

                    var yeniBitmap = new BitmapImage();
                    yeniBitmap.BeginInit();
                    yeniBitmap.CacheOption = BitmapCacheOption.OnLoad; // Bellek sızıntısını önler
                    yeniBitmap.StreamSource = ms;
                    yeniBitmap.EndInit();

                    // --- FADE-OUT (Kararma) Animasyonu ---
                    var fadeOut = new DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.0,
                        Duration = TimeSpan.FromMilliseconds(800) // 800 ms'de silikleşir
                    };

                    // Resim tamamen silikleştiğinde yeni resmi yükleyip Fade-in başlatıyoruz
                    fadeOut.Completed += (s, e) =>
                    {
                        imgCanli.Source = yeniBitmap; // Yeni resmi atıyoruz

                        // --- FADE-IN (Açılma) Animasyonu ---
                        var fadeIn = new DoubleAnimation
                        {
                            From = 0.0,
                            To = 1.0,
                            Duration = TimeSpan.FromMilliseconds(1000) // 100 ms'de netleşir
                        };

                        imgCanli.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);
                    };

                    // Animasyonu başlat
                    imgCanli.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeOut);
                }
                catch (Exception ex)
                {
                    EkranaYazdir($"[RESİM HATASI] {ex.Message}");
                }
            });
        }

        private void EkranaYazdir(string mesaj)
        {
            // Arayüzde log alanı olmadığı için metinleri Visual Studio Output penceresine yazdırıyoruz
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {mesaj}");
        }
    }
}