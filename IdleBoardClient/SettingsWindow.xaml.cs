using System.Windows;

namespace SseWpfApp
{
    public partial class SettingsWindow : Window
    {
        public string ServerUrl { get; private set; }

        public SettingsWindow(string mevcutUrl)
        {
            InitializeComponent();
        // assign property to satisfy non-nullable requirement
        ServerUrl = mevcutUrl;
        txtServerUrl.Text = mevcutUrl;
        }

        private void btnKaydet_Click(object sender, RoutedEventArgs e)
        {
            string girilenUrl = txtServerUrl.Text.Trim();

            if (!string.IsNullOrEmpty(girilenUrl))
            {
                ServerUrl = girilenUrl;
                this.DialogResult = true;
                this.Close();
            }
            else
            {
                System.Windows.MessageBox.Show("Lütfen geçerli bir sunucu adresi girin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnIptal_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}