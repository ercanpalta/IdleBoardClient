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
            // SSE dinleyicisini durdur; arka plan görevi uygulama kapanırken sürmesin
            _sseCancellationTokenSource?.Cancel();

            // Tray Icon'u bellekten temizle ve kapat
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            foreach (var icon in _durumIkonlari.Values) icon.Dispose();
            _durumIkonlari.Clear();
            System.Windows.Application.Current.Shutdown();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        // Durum ikonları yalnızca bir kez üretilip yeniden kullanılır; her durum değişiminde yeni GDI nesnesi sızdırılmaz
        private readonly System.Collections.Generic.Dictionary<System.Drawing.Color, System.Drawing.Icon> _durumIkonlari = new();

        private System.Drawing.Icon OlusturDurumIkonu(System.Drawing.Color renk)
        {
            if (_durumIkonlari.TryGetValue(renk, out var cached)) return cached;
            var icon = UretDurumIkonu(renk);
            _durumIkonlari[renk] = icon;
            return icon;
        }

        // 16x16 piksel boyutunda dinamik renkli daire ikon oluşturur
        private System.Drawing.Icon UretDurumIkonu(System.Drawing.Color renk)
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

        // NotifyIcon.Text en fazla 127 karakter kabul eder; aşan metin ArgumentOutOfRangeException fırlatır
        private void SetTrayText(string text)
        {
            if (_notifyIcon == null) return;
            const int maxLength = 127;
            _notifyIcon.Text = text.Length <= maxLength ? text : text.Substring(0, maxLength - 3) + "...";
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
                            SetTrayText($"IdleBoard: Bağlantı Aktif\n{ServerUrl}");
                            break;

                        case BaglantiDurumu.Baglaniyor:
                            _notifyIcon.Icon = OlusturDurumIkonu(System.Drawing.Color.Orange);
                            SetTrayText(string.IsNullOrWhiteSpace(mesaj)
                                ? "IdleBoard: Bağlanıyor..."
                                : $"IdleBoard: Bağlanıyor...\n{mesaj}");
                            break;

                        case BaglantiDurumu.Koptu:
                            _notifyIcon.Icon = OlusturDurumIkonu(System.Drawing.Color.Red);
                            // NotifyIcon.Text has a strict max length; show only a short status here
                            SetTrayText(cacheCount > 0
                                ? $"IdleBoard: Çevrimdışı ({cacheCount} Önbellek)"
                                : "IdleBoard: Bağlantı Kesildi!");
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
