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

            // 1. UI Thread üzerindeki yakalanmayan hatalar
            this.DispatcherUnhandledException += (s, ev) =>
            {
                Logger.LogError("Yakalanmayan UI Hatası (DispatcherUnhandledException)", ev.Exception);
                ev.Handled = true; // Uygulamanın aniden kapanmasını engeller
            };

            // 2. Arka plan / Task Thread üzerindeki yakalanmayan hatalar
            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                Logger.LogError("Yakalanmayan Arka Plan Hatası (UnobservedTaskException)", ev.Exception);
                ev.SetObserved();
            };

            // 3. AppDomain genelindeki yakalanmayan ağır hatalar
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                if (ev.ExceptionObject is Exception ex)
                {
                    Logger.LogError("Kritik Uygulama Hatası (UnhandledException)", ex);
                }
            };

            Logger.LogInfo("Kiosk Uygulaması Başlatıldı.");

            // Kiosk modunda ekranın ve sistemin kapanmasını engelle
            PowerManagement.UykuModunuEngelle();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.LogInfo("Kiosk Uygulaması Kapatıldı.");

            // Uygulama kapanırken Windows güç ayarlarını normale döndür
            PowerManagement.UykuModunuSerbestBirak();

            base.OnExit(e);
        }
    }

}
