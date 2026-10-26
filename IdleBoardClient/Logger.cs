using System;
using System.IO;

public static class Logger
{
    private static readonly object _lockObj = new object();
    private static readonly string LogFolderPath = InitializeLogFolder();

    private static string InitializeLogFolder()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = Path.Combine(localAppData, "IdleBoardClient", "logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            return logDir;
        }
        catch
        {
            string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            try { Directory.CreateDirectory(fallback); } catch { }
            return fallback;
        }
    }

    public static void LogError(string message, Exception? ex = null)
    {
        WriteToFile("ERROR", message, ex);
    }

    public static void LogInfo(string message)
    {
        WriteToFile("INFO", message, null);
    }

    private static void WriteToFile(string level, string message, Exception? ex)
    {
        try
        {
            lock (_lockObj)
            {
                if (!Directory.Exists(LogFolderPath))
                {
                    Directory.CreateDirectory(LogFolderPath);
                }

                string fileName = $"log_{DateTime.Now:yyyy-MM-dd}.txt";
                string filePath = Path.Combine(LogFolderPath, fileName);

                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

                if (ex != null)
                {
                    logContent += $"\nException: {ex.GetType().Name} - {ex.Message}\nStackTrace:\n{ex.StackTrace}\n";
                }

                logContent += new string('-', 80) + "\n";

                File.AppendAllText(filePath, logContent);
            }
        }
        catch
        {
            // Log yazarken hata oluşursa uygulamanın çökmesini engelliyoruz
        }
    }

    /// <param name="retentionDays">Logların saklanacağı gün sayısı (Varsayılan: 30)</param>
    public static void CleanOldLogs(int retentionDays = 30)
    {
        // UI Thread'i engellememek için silme işlemini arka plan Görevinde (Task) çalıştırıyoruz
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(LogFolderPath)) return;

                DirectoryInfo directoryInfo = new DirectoryInfo(LogFolderPath);
                FileInfo[] files = directoryInfo.GetFiles("log_*.txt");

                DateTime thresholdDate = DateTime.Now.AddDays(-retentionDays);

                int deletedCount = 0;
                foreach (FileInfo file in files)
                {
                    // Dosyanın son yazılma veya oluşturulma tarihi sınır tarihten eski ise sil
                    if (file.LastWriteTime < thresholdDate)
                    {
                        file.Delete();
                        deletedCount++;
                    }
                }

                if (deletedCount > 0)
                {
                    LogInfo($"{deletedCount} adet eski log dosyası otomatik temizlendi ({retentionDays} günden eski).");
                }
            }
            catch (Exception ex)
            {
                LogError("Eski log dosyaları temizlenirken hata oluştu.", ex);
            }
        });
    }
}