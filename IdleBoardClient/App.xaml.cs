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
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Tek örnek: aynı kullanıcı oturumunda ikinci kopya kiosk ekranında çakışmasın
            _singleInstanceMutex = new Mutex(true, @"Local\IdleBoardClient_SingleInstance", out bool ilkOrnek);
            if (!ilkOrnek)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Sürüm değişince user.config yolu değişir; önceki sürümün ayarlarını (ServerUrl, IsFirstRun...) taşı
            try
            {
                if (IdleBoardClient.Settings.Default.UpgradeNeeded)
                {
                    IdleBoardClient.Settings.Default.Upgrade();
                    IdleBoardClient.Settings.Default.UpgradeNeeded = false;
                    IdleBoardClient.Settings.Default.Save();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Ayarlar önceki sürümden taşınamadı", ex);
            }

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

            Logger.LogInfo("Uygulama Başlatıldı.");

            // İlk çalıştırma ise varsayılan olarak Windows başlangıcına ekler
            AutoStartHelper.IlkKurulumAyariniKontrolEt();

            // 30 günden eski log dosyalarını temizler (isteğe bağlı gün sayısını değiştirebilirsiniz)
            Logger.CleanOldLogs(retentionDays: 30);

            // Ekranın ve sistemin kapanmasını engelle
            PowerManagement.UykuModunuEngelle();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex == null)
            {
                base.OnExit(e);
                return;
            }
            Logger.LogInfo("Uygulama Kapatıldı.");

            // Uygulama kapanırken Windows güç ayarlarını normale döndür
            PowerManagement.UykuModunuSerbestBirak();

            base.OnExit(e);
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

}
