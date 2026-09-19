using Microsoft.Win32;
using System;
using System.Reflection;

public static class AutoStartHelper
{
    private const string RunRegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string AppName = "WpfKioskBillboard"; // Registry'de görünecek kayıt adı

    /// <summary>
    /// Uygulamanın Windows başlangıcına eklenip eklenmediğini kontrol eder.
    /// </summary>
    public static bool OtomatikBaslatmaAktifMi()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch (Exception ex)
        {
            Logger.LogError("Registry kontrol edilirken hata oluştu.", ex);
            return false;
        }
    }

    /// <summary>
    /// Uygulamayı Windows başlangıcına ekler veya başlangıçtan kaldırır.
    /// </summary>
    /// <param name="aktifEt">True ise ekler, False ise kaldırır.</param>
    public static void OtomatikBaslatmayiAyarla(bool aktifEt)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, true);
            if (key == null) return;

            if (aktifEt)
            {
                // Çalışan .exe dosyasının tam yolunu alıyoruz
                string exePath = Assembly.GetExecutingAssembly().Location;

                // .NET Core / .NET 8 için .dll yerine .exe yolunu garantiye almak
                if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = exePath.Substring(0, exePath.Length - 4) + ".exe";
                }

                // Yolu tırnak içine alarak parametre karmasasını engelliyoruz
                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.LogInfo("Uygulama Windows başlangıcına eklendi.");
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                    Logger.LogInfo("Uygulama Windows başlangıcından kaldırıldı.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Windows başlangıç Registry ayarı değiştirilirken hata oluştu.", ex);
        }
    }

    /// İlk kurulumda uygulamanın otomatik başlangıç kaydını varsayılan olarak oluşturur.
    /// </summary>
    public static void IlkKurulumAyariniKontrolEt()
    {
        // Eğer kullanıcı daha önce uygulamayı açmadıysa (Settings üzerinde IsFirstRun kontrolü)
        if (IdleBoardClient.Settings.Default.IsFirstRun)
        {
            // Varsayılan olarak Windows başlangıcına ekliyoruz
            OtomatikBaslatmayiAyarla(true);

            // İlk kurulum tamamlandı işaretini koyup kaydediyoruz
            IdleBoardClient.Settings.Default.IsFirstRun = false;
            IdleBoardClient.Settings.Default.Save();
        }
    }

}