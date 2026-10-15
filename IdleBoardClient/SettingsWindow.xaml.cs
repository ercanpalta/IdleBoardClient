using System;
using System.Windows;

namespace SseWpfApp
{
    public partial class SettingsWindow : Window
    {
        public string ServerUrl { get; private set; }
        public int IdleTimeSeconds { get; private set; }

        public SettingsWindow(string mevcutUrl, int mevcutIdleSuresi = 5)
        {
            InitializeComponent();

            // Pencere açılırken Registry durumunu kontrol edip CheckBox'ı işaretliyoruz
            ChkAutoStart.IsChecked = AutoStartHelper.OtomatikBaslatmaAktifMi();

            // Sunucu adresi alanı
            ServerUrl = mevcutUrl;
            txtServerUrl.Text = mevcutUrl;

            // Boşta kalma süresi alanı
            IdleTimeSeconds = mevcutIdleSuresi > 0 ? mevcutIdleSuresi : 5;
            txtIdleTime.Text = IdleTimeSeconds.ToString();
        }

        private void btnKaydet_Click(object sender, RoutedEventArgs e)
        {
            string girilenUrl = txtServerUrl.Text.Trim();
            string girilenIdleStr = txtIdleTime.Text.Trim();

            // 1. URL Boş mu veya Geçerli bir URL formatında mı? (Sıralı Kontrol)
            if (string.IsNullOrEmpty(girilenUrl) || !Uri.IsWellFormedUriString(girilenUrl, UriKind.Absolute))
            {
                System.Windows.MessageBox.Show("Lütfen geçerli bir sunucu adresi girin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. Boşta kalma süresi doğrulaması (En az 1 saniye, en çok 86400 saniye / 24 saat)
            if (!int.TryParse(girilenIdleStr, out int girilenIdle) || girilenIdle < 1 || girilenIdle > 86400)
            {
                System.Windows.MessageBox.Show("Lütfen geçerli bir boşta kalma süresi girin (1 - 86400 saniye arası).", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 3. Değerleri ata ve kaydet
            ServerUrl = girilenUrl;
            IdleTimeSeconds = girilenIdle;

            Logger.LogInfo($"Sunucu adresi güncellendi: {girilenUrl}");
            Logger.LogInfo($"Boşta kalma süresi güncellendi: {girilenIdle} saniye");

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