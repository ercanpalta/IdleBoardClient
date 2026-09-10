using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SseWpfApp
{
    public partial class MainWindow : Window
    {
        private const string ServerUrl = "http://localhost:3000/events";
        
        // Mouse başlangıç pozisyonunu ve takibini yapacak değişkenler
        private Point? _baslangicMousePozisyonu = null;
        private const double HareketeDuyarlilikEsigi = 20; // Kaç piksel hareket edince kapansın?

        public MainWindow()
        {
            InitializeComponent();

            // Mouse hareketlerini pencere seviyesinde dinliyoruz
            this.PreviewMouseMove += MainWindow_PreviewMouseMove;
        }

        private void btnBaslat_Click(object sender, RoutedEventArgs e)
        {
            btnBaslat.IsEnabled = false;
            txtDurum.Text = "Durum: Sunucuya bağlanılıyor...";
            txtDurum.Foreground = System.Windows.Media.Brushes.Orange;

            Task.Run(async () => await StartSseListenerAsync());
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

                    DurumGuncelle("Durum: Bağlantı Aktif (Resimler Alınıyor)", System.Windows.Media.Brushes.LightGreen);

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();

                        if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("data: "))
                        {
                            string jsonString = line.Substring(6);

                            // JSON Verisini ayrıştırıyoruz
                            using var doc = JsonDocument.Parse(jsonString);
                            var root = doc.RootElement;

                            string zaman = root.GetProperty("zaman").GetString();
                            string mesaj = root.GetProperty("mesaj").GetString();
                            string base64Image = root.GetProperty("resimData").GetString();

                            // Resmi ekranda göster
                            ResmiGuncelle(base64Image);
                            EkranaYazdir($"{zaman} - {mesaj}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    DurumGuncelle("Durum: Bağlantı Koptu! Tekrar deneniyor...", System.Windows.Media.Brushes.IndianRed);
                    EkranaYazdir($"[HATA] {ex.Message}");
                    await Task.Delay(5000);
                }
            }
        }

        // Base64 String verisini WPF Image kontrolüne basan metod
        private void ResmiGuncelle(string base64String)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    byte[] imageBytes = Convert.FromBase64String(base64String);
                    using var ms = new MemoryStream(imageBytes);

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Hafıza sızıntısını önler
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();

                    imgCanli.Source = bitmap; // Arayüzdeki Image kontrolüne aktar
                }
                catch (Exception ex)
                {
                    EkranaYazdir($"[RESİM İŞLEME HATASI] {ex.Message}");
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

        private void DurumGuncelle(string mesaj, System.Windows.Media.Brush renk)
        {
            Dispatcher.Invoke(() =>
            {
                txtDurum.Text = mesaj;
                txtDurum.Foreground = renk;
            });
        }

        private void MainWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Mevcut mouse konumunu alıyoruz
            Point mevcutPozisyon = e.GetPosition(this);

            // Başlangıç konumunu kaydediyoruz (Uygulama açılışında ilk konum)
            if (!_baslangicMousePozisyonu.HasValue)
            {
                _baslangicMousePozisyonu = mevcutPozisyon;
                return;
            }

            // İlk konum ile mevcut konum arasındaki farkı hesaplıyoruz
            double xFarki = Math.Abs(mevcutPozisyon.X - _baslangicMousePozisyonu.Value.X);
            double yFarki = Math.Abs(mevcutPozisyon.Y - _baslangicMousePozisyonu.Value.Y);

            // Belirlenen piksel eşiğinden fazla hareket edildiyse uygulamayı kapat
            if (xFarki > HareketeDuyarlilikEsigi || yFarki > HareketeDuyarlilikEsigi)
            {
                Application.Current.Shutdown();
            }
        }
    }
}