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

            // assign property to satisfy non-nullable requirement
            ServerUrl = mevcutUrl;
            txtServerUrl.Text = mevcutUrl;
        }

        private void btnKaydet_Click(object sender, RoutedEventArgs e)
        {
            string girilenUrl = txtServerUrl.Text.Trim();

            // 1. URL Boş mu veya Geçerli bir URL formatında mı? (Sıralı Kontrol)
            if (string.IsNullOrEmpty(girilenUrl) || !Uri.IsWellFormedUriString(girilenUrl, UriKind.Absolute))
            {
                System.Windows.MessageBox.Show("Lütfen geçerli bir sunucu adresi girin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);

                // Hata durumunda METODU BURADA KESİYORUZ (Aşağıya geçmesini engeller)
                return;
            }

            // 2. Yalnızca URL geçerliyse kaydetme işlemlerine geç
            ServerUrl = girilenUrl;

            Logger.LogInfo($"Sunucu adresi güncellendi: {girilenUrl}");

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