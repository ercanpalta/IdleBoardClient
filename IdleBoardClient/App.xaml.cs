using System.Configuration;
using System.Data;
using System.Windows;

namespace IdleBoardClient
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Kiosk modunda ekranın ve sistemin kapanmasını engelle
            PowerManagement.UykuModunuEngelle();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Uygulama kapanırken Windows güç ayarlarını normale döndür
            PowerManagement.UykuModunuSerbestBirak();

            base.OnExit(e);
        }
    }

}
