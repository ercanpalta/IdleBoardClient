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
            string path = uri.AbsolutePath.ToLowerInvariant();
            string repo = OfficialRepo.ToLowerInvariant();
            return (host == "github.com" && path.StartsWith($"/{repo}/releases/download/", StringComparison.Ordinal))
                || (host == "api.github.com" && path.StartsWith($"/repos/{repo}/", StringComparison.Ordinal));
        }

        private const string OfficialRepo = "ercanpalta/IdleBoardClient";

        private static bool IsDirectoryWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write_test_" + Path.GetRandomFileName());
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
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

            if (string.Equals(targetVersion?.TrimStart('v', 'V'), CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInfo($"[Otomatik Guncelleme] Hedef surum ({targetVersion}) zaten yuklu, guncelleme atlandi.");
                return false;
            }

            if (!IsDirectoryWritable(AppDomain.CurrentDomain.BaseDirectory))
            {
                Logger.LogInfo("[Otomatik Guncelleme] Uygulama klasorune yazma izni yok; guncelleme atlandi (yonetici olarak yeniden kurulum gerekebilir).");
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
                    using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    await using var zipSource = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var zipDest = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                    await zipSource.CopyToAsync(zipDest).ConfigureAwait(false);
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
set /a waited=0
:waitloop
tasklist /fi ""PID eq {currentProcessId}"" | findstr ""{currentProcessId}"" >nul
if errorlevel 1 goto closed
set /a waited+=1
if %waited% geq 60 goto giveup
timeout /t 1 /nobreak >nul
goto waitloop

:giveup
echo [IdleBoard Güncelleyici] Uygulama kapanmadı, güncelleme iptal edildi.
rd /s /q ""{tempDir}"" >nul 2>&1
exit /b 1

:closed
timeout /t 1 /nobreak >nul

echo [IdleBoard Güncelleyici] Yeni sürüm arşivi açılıyor...
powershell -NoProfile -NonInteractive -Command ""Expand-Archive -LiteralPath '{zipPath.Replace("'", "''")}' -DestinationPath '{extractedPath.Replace("'", "''")}' -Force""
if errorlevel 1 (
    echo [IdleBoard Güncelleyici] Arşiv açılamadı, mevcut sürüm yeniden başlatılıyor.
    goto restart
)

echo [IdleBoard Güncelleyici] Dosyalar güncelleniyor...
robocopy ""{extractedPath}"" ""{currentAppDir}"" /E /R:3 /W:2 /XD cache logs /XF watermark_cached.png *.user.config /NFL /NDL /NJH /NJS >nul 2>&1
if errorlevel 8 echo [IdleBoard Güncelleyici] Dosyalar kopyalanırken hata oluştu (robocopy kodu %errorlevel%).

:restart
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

                using (Process.Start(startInfo)) { }

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
