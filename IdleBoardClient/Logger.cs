using System;
using System.IO;

public static class Logger
{
    private static readonly object _lockObj = new object();
    private static readonly string LogFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

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
}