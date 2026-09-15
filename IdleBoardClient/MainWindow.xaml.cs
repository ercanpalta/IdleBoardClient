using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Forms ve WPF Isim Çakışmasını Önlemek İçin Alias
using Forms = System.Windows.Forms;

namespace SseWpfApp
{
    public partial class MainWindow : Window
    {
        private const string ServerUrl = "http://localhost:3000/events";

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

            var itemCikis = new Forms.ToolStripMenuItem("Çıkış");
            itemCikis.Click += (s, e) => UygulamayiTamamenKapat();

            contextMenu.Items.Add(itemGoster);
            contextMenu.Items.Add(new Forms.ToolStripSeparator());
            contextMenu.Items.Add(itemCikis);

            _notifyIcon.ContextMenuStrip = contextMenu;

            // Simgeye çift tıklandığında da açılması için
            _notifyIcon.DoubleClick += (s, e) => UygulamayiAc();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EkranaYazdir("Sunucuya bağlanılıyor...");
            Task.Run(async () => await StartSseListenerAsync());
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

        private async Task StartSseListenerAsync()
        {
            while (true)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromMilliseconds(-1);

                    using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl);
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    EkranaYazdir("Bağlantı aktif. Resimler alınıyor...");

                    // Use ReadLineAsync() and break when it returns null instead of EndOfStream
                    while (true)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;

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
                catch (Exception ex)
                {
                    EkranaYazdir($"[HATA] {ex.Message}. 5s sonra tekrar deneniyor...");
                    await Task.Delay(5000);
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

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();

                    imgCanli.Source = bitmap;
                }
                catch (Exception ex)
                {
                    EkranaYazdir($"[RESİM HATASI] {ex.Message}");
                }
            });
        }

        private void EkranaYazdir(string mesaj)
        {
            Dispatcher.Invoke(() =>
            {
                listBoxLog.Items.Add(mesaj);
                listBoxLog.ScrollIntoView(listBoxLog.Items[listBoxLog.Items.Count - 1]);
            });
        }
    }
}