using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace IdleBoardClient
{
    /// <summary>
    /// GitHub Releases üzerinden yayınlanan yeni sürümleri arka planda indiren
    /// ve kesintisiz şekilde güncelleyip uygulamayı yeniden başlatan yardımcı sınıf.
    /// </summary>
    public static class AutoUpdater
    {
        public static string CurrentVersion
        {
            get
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            }
        }

        private static int _updateInProgress;

        // Güncelleme paketi yalnızca GitHub'ın HTTPS release/arşiv adreslerinden kabul edilir.
        private static bool IsTrustedDownloadUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;

            string host = uri.Host.ToLowerInvariant();
            return host == "github.com"
                || host == "api.github.com"
                || host == "codeload.github.com"
                || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        public static async Task<bool> ApplyUpdateAsync(string downloadUrl, string targetVersion)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return false;

            if (!IsTrustedDownloadUrl(downloadUrl))
            {
                Logger.LogInfo($"[Otomatik Güncelleme] Güvenilmeyen indirme adresi reddedildi: {downloadUrl}");
                return false;
            }

            if (Interlocked.Exchange(ref _updateInProgress, 1) == 1)
            {
                Logger.LogInfo("[Otomatik Güncelleme] Bir güncelleme zaten sürüyor, yeni istek yok sayıldı.");
                return false;
            }

            string? tempDir = null;
            try
            {
                Logger.LogInfo($"[Otomatik Güncelleme] v{targetVersion} sürümü indiriliyor: {downloadUrl}");

                tempDir = Path.Combine(Path.GetTempPath(), "IdleBoardClient_Update_" + Path.GetRandomFileName().Replace(".", ""));
                Directory.CreateDirectory(tempDir);

                string zipPath = Path.Combine(tempDir, "update.zip");

                using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IdleBoardClient-Updater");
                    using var response = await httpClient.GetAsync(downloadUrl).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    byte[] zipBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    await File.WriteAllBytesAsync(zipPath, zipBytes).ConfigureAwait(false);
                }

                long fileSizeKb = new FileInfo(zipPath).Length / 1024;
                Logger.LogInfo($"[Otomatik Güncelleme] Güncelleme paketi başarıyla indirildi ({fileSizeKb} KB). Güncelleyici komutu hazırlanıyor.");

                string currentAppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                int currentProcessId = Process.GetCurrentProcess().Id;

                // Geçici güncelleme komut dosyası oluştur
                string scriptPath = Path.Combine(tempDir, "apply_update.cmd");
                string extractedPath = Path.Combine(tempDir, "extracted");

                string scriptContent = $@"@echo off
chcp 65001 >nul
echo [IdleBoard Güncelleyici] Uygulamanın kapanması bekleniyor (PID: {currentProcessId})...
:waitloop
tasklist /fi ""PID eq {currentProcessId}"" | findstr ""{currentProcessId}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto waitloop
)
timeout /t 1 /nobreak >nul

echo [IdleBoard Güncelleyici] Yeni sürüm arşivi açılıyor...
powershell -NoProfile -NonInteractive -Command ""Expand-Archive -LiteralPath '{zipPath.Replace("'", "''")}' -DestinationPath '{extractedPath.Replace("'", "''")}' -Force""

echo [IdleBoard Güncelleyici] Dosyalar güncelleniyor...
robocopy ""{extractedPath}"" ""{currentAppDir}"" /E /XD cache logs /XF watermark_cached.png *.user.config /NFL /NDL /NJH /NJS >nul 2>&1

echo [IdleBoard Güncelleyici] Uygulama yeniden başlatılıyor...
start """" ""{Path.Combine(currentAppDir, "IdleBoardClient.exe")}""

timeout /t 2 /nobreak >nul
rd /s /q ""{tempDir}"" >nul 2>&1
exit
";

                await File.WriteAllTextAsync(scriptPath, scriptContent).ConfigureAwait(false);

                // Scripti çalıştır ve mevcut uygulamayı kapat
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{scriptPath}\"\"",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(startInfo);

                Logger.LogInfo("[Otomatik Güncelleme] Güncelleyici başlatıldı. Mevcut uygulama kapatılıyor.");
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("[Otomatik Güncelleme] Güncelleme uygulanırken hata oluştu.", ex);
                if (tempDir != null)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                Interlocked.Exchange(ref _updateInProgress, 0);
                return false;
            }
        }
    }
}
