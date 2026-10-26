using IdleBoardClient;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace SseWpfApp
{
    public partial class SettingsWindow : Window
    {
        public string ServerUrl { get; private set; }

        public SettingsWindow(string mevcutUrl)
        {
            InitializeComponent();

            // Pencere açılırken Registry durumunu kontrol edip CheckBox'ı işaretliyoruz
            ChkAutoStart.IsChecked = AutoStartHelper.OtomatikBaslatmaAktifMi();

            // Sunucu adresi alanı
            ServerUrl = mevcutUrl;
            txtServerUrl.Text = mevcutUrl;
        }

        private async void btnOtomatikBul_Click(object sender, RoutedEventArgs e)
        {
            btnOtomatikBul.IsEnabled = false;
            btnOtomatikBul.Content = "⏳ Aranıyor...";

            try
            {
                var discovered = await ServerDiscoveryService.DiscoverServerAsync(TimeSpan.FromSeconds(2.5));
                if (discovered != null)
                {
                    txtServerUrl.Text = discovered.EventsUrl;
                    System.Windows.MessageBox.Show(
                        $"Sunucu yerel ağda başarıyla bulundu!\n\nAdres: {discovered.ServerUrl}\nCihaz: {discovered.Hostname}",
                        "Sunucu Bulundu",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        "Yerel ağda aktif bir IdleBoard sunucusu bulunamadı.\n\nLütfen sunucu bilgisayarının açık ve aynı ağa bağlı olduğundan emin olun.",
                        "Sunucu Bulunamadı",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                btnOtomatikBul.IsEnabled = true;
                btnOtomatikBul.Content = "🔍 Otomatik Bul";
            }
        }

        private void btnKaydet_Click(object sender, RoutedEventArgs e)
        {
            string girilenUrl = txtServerUrl.Text.Trim();

            // URL Boş mu veya Geçerli bir URL formatında mı? (Sıralı Kontrol)
            if (string.IsNullOrEmpty(girilenUrl) || !Uri.IsWellFormedUriString(girilenUrl, UriKind.Absolute))
            {
                System.Windows.MessageBox.Show("Lütfen geçerli bir sunucu adresi girin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ServerUrl = ServerDiscoveryService.NormalizeEventsUrl(girilenUrl);
            Logger.LogInfo($"Sunucu adresi güncellendi: {ServerUrl}");

            // Auto-Start ayarını uygula
            bool autoStartDurum = ChkAutoStart.IsChecked ?? false;
            AutoStartHelper.OtomatikBaslatmayiAyarla(autoStartDurum);

            this.DialogResult = true;
            this.Close();
        }

        private void btnIptal_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
