using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace IdleBoardClient
{
    /// <summary>
    /// Sunucudan gelen görselleri yerel diskte önbelleğe alan ve
    /// çevrimdışı (offline) modda kesintisiz slayt gösterisi sağlayan yardımcı sınıf.
    /// </summary>
    public static class ImageCacheManager
    {
        private static readonly object _lockObj = new object();
        public static readonly string CacheDirectory = InitializeCacheDirectory();

        private static string InitializeCacheDirectory()
        {
            try
            {
                // Standart Windows Kullanıcı Veri Alanı (%LOCALAPPDATA%\IdleBoardClient\cache)
                // Her Windows kullanıcısı için (yönetici olmasa bile) kesinlikle tam yazma izinlidir.
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appDataDir = Path.Combine(localAppData, "IdleBoardClient", "cache");
                if (!Directory.Exists(appDataDir))
                {
                    Directory.CreateDirectory(appDataDir);
                }

                // Eski konfigürasyondan (AppDomain BaseDirectory\cache) kalan dosyalar varsa yeni konuma taşı
                string legacyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                if (Directory.Exists(legacyDir) && !string.Equals(legacyDir, appDataDir, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(legacyDir))
                        {
                            string target = Path.Combine(appDataDir, Path.GetFileName(f));
                            if (!File.Exists(target))
                            {
                                File.Copy(f, target, true);
                            }
                        }
                    }
                    catch { }
                }

                return appDataDir;
            }
            catch
            {
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                try { Directory.CreateDirectory(fallback); } catch { }
                return fallback;
            }
        }

        public static string ExtractBaseAuthority(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "http://127.0.0.1:3000";
            string clean = url.Trim();
            if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                clean = "http://" + clean;
            }
            try
            {
                var uri = new Uri(clean);
                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                int slashIdx = clean.IndexOf('/', 8);
                return slashIdx > 0 ? clean.Substring(0, slashIdx) : clean;
            }
        }

        private static readonly HashSet<string> DesteklenenUzantilar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".mp4"
        };

        public static bool IsVideoFile(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            return fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        }

        private const int MaxOnbellekDosyaSayisi = 200;

        static ImageCacheManager()
        {
            OnbellekKlasorunuOlustur();
        }

        public static void OnbellekKlasorunuOlustur()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                {
                    Directory.CreateDirectory(CacheDirectory);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Önbellek klasörü oluşturulurken hata meydana geldi.", ex);
            }
        }

        /// <summary>
        /// Base64 formatındaki görseli diskteki önbelleğe asenkron olarak kaydeder.
        /// Dosya adı belirtilmemişse Base64 verisinin MD5 özetinden dosya adı üretir.
        /// </summary>
        public static async Task<string?> SaveImageAsync(string? originalFileName, string? base64Data)
        {
            if (string.IsNullOrWhiteSpace(base64Data))
                return null;

            try
            {
                OnbellekKlasorunuOlustur();

                int commaIdx = base64Data.IndexOf(',');
                string cleanBase64 = (commaIdx >= 0) ? base64Data.Substring(commaIdx + 1) : base64Data;
                byte[] imageBytes = Convert.FromBase64String(cleanBase64.Trim());

                string safeFileName = GuvenliDosyaAdiUret(originalFileName, imageBytes);
                string targetPath = Path.Combine(CacheDirectory, safeFileName);

                // Dosya zaten varsa ve boyutu aynıysa diske gereksiz I/O yazımı yapma, sadece erişim zamanını güncelle
                if (File.Exists(targetPath))
                {
                    var fileInfo = new FileInfo(targetPath);
                    if (fileInfo.Length == imageBytes.Length)
                    {
                        File.SetLastWriteTime(targetPath, DateTime.Now);
                        return targetPath;
                    }
                }

                await File.WriteAllBytesAsync(targetPath, imageBytes);
                Logger.LogInfo($"Görsel önbelleğe kaydedildi: {safeFileName} ({imageBytes.Length / 1024} KB)");

                // Önbellek dosya sınırını kontrol et
                EskiOnbellekDosyalariniTemizle();

                return targetPath;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Görsel önbelleğe kaydedilirken hata oluştu: {originalFileName}", ex);
                return null;
            }
        }

        /// <summary>
        /// Sunucudaki aktif yayın görsel listesi ile yerel disk önbelleğini senkronize eder.
        /// Sunucuda artık yayında veya mevcut olmayan eski görseller önbellekten silinir.
        /// Eksik olan yayındaki görseller ve videolar sunucudan doğrudan indirilip yerel diske kaydedilir.
        /// </summary>
        public static async Task<bool> SyncActiveImagesAsync(
            string serverBaseUrl,
            IReadOnlyList<string> activeFileNames,
            Action<int, int, string>? onProgress = null,
            CancellationToken ct = default)
        {
            if (activeFileNames == null) return false;

            return await Task.Run(async () =>
            {
                lock (_lockObj)
                {
                    OnbellekKlasorunuOlustur();
                }

                var activeSet = new HashSet<string>(activeFileNames, StringComparer.OrdinalIgnoreCase);

                // 1. ADIM: Artık sunucuda seçili olmayan eski önbellek dosyalarını temizle (Purge)
                try
                {
                    if (Directory.Exists(CacheDirectory))
                    {
                        var dir = new DirectoryInfo(CacheDirectory);
                        foreach (var file in dir.GetFiles())
                        {
                            if (DesteklenenUzantilar.Contains(file.Extension) && !activeSet.Contains(file.Name))
                            {
                                try
                                {
                                    file.Delete();
                                    Logger.LogInfo($"[Önbellek Temizleme] Sunucuda seçili olmayan eski dosya silindi: {file.Name}");
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError($"[Önbellek Temizleme] Dosya silinirken hata: {file.Name}", ex);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("[Önbellek Temizleme] Önbellek dizini taranırken hata.", ex);
                }

                if (activeFileNames.Count == 0) return true;

                // 2. ADIM: Önbellekte eksik olan aktif görselleri ve videoları HTTP üzerinden yerel diske indir
                string baseUriString = ExtractBaseAuthority(serverBaseUrl);

                // Büyük video ve yüksek çözünürlüklü görseller için geniş zaman aşımı (120 saniye)
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

                int totalCount = activeFileNames.Count;
                int currentCount = 0;

                foreach (var fileName in activeFileNames)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(fileName)) continue;

                    string? localPath = ResolveCachePath(fileName);
                    if (localPath == null)
                    {
                        Logger.LogInfo($"[Önbellek] Geçersiz dosya adı atlandı: {fileName}");
                        continue;
                    }
                    bool alreadyExists = false;

                    if (File.Exists(localPath))
                    {
                        var fi = new FileInfo(localPath);
                        if (fi.Length > 0)
                        {
                            alreadyExists = true;
                        }
                    }

                    if (!alreadyExists)
                    {
                        try
                        {
                            string downloadUrl = $"{baseUriString.TrimEnd('/')}/images/{Uri.EscapeDataString(fileName)}";
                            var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                string tempPath = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                try
                                {
                                    using (var sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                                    using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                                    {
                                        await sourceStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
                                    }

                                    File.Move(tempPath, localPath, overwrite: true);
                                    Logger.LogInfo($"[Önbellek Ön-Yükleme] Dosya diske indirildi: {fileName}");
                                }
                                catch (Exception writeEx)
                                {
                                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                                    Logger.LogError($"[Önbellek Ön-Yükleme] Dosya yazılamadı: {fileName}", writeEx);
                                }
                            }
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            Logger.LogError($"[Önbellek Ön-Yükleme] Dosya indirilemedi: {fileName}", ex);
                        }
                    }

                    currentCount++;
                    onProgress?.Invoke(currentCount, totalCount, fileName);
                }

                return true;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Belirli bir dosyanın yerel diskte bulunduğundan emin olur; eksikse sunucudan hemen indirir.
        /// Asla doğrudan ağdan akış oynatılmaz, dosya mutlaka önce diske indirilir.
        /// </summary>
        public static async Task<string?> EnsureFileDownloadedAsync(string serverBaseUrl, string fileName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            OnbellekKlasorunuOlustur();
            string? localPath = ResolveCachePath(fileName);
            if (localPath == null)
            {
                Logger.LogInfo($"[Önbellek] Geçersiz dosya adı reddedildi: {fileName}");
                return null;
            }

            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                return localPath;
            }

            try
            {
                string baseUriString = ExtractBaseAuthority(serverBaseUrl);
                string downloadUrl = $"{baseUriString.TrimEnd('/')}/images/{Uri.EscapeDataString(fileName)}";
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string tempPath = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                        using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            await sourceStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
                        }

                        File.Move(tempPath, localPath, overwrite: true);
                        Logger.LogInfo($"[Önbellek] Dosya talep üzerine diske kaydedildi: {fileName}");
                        return localPath;
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                        Logger.LogError($"[Önbellek] Dosya diske yazılamadı: {fileName}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Önbellek] Dosya indirilirken hata: {fileName}", ex);
            }

            return null;
        }

        /// <summary>
        /// Önbellekteki tüm geçerli görsel dosyalarının tam yollarını döndürür.
        /// </summary>
        public static IReadOnlyList<string> GetCachedImageFiles()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                    return Array.Empty<string>();

                var files = Directory.GetFiles(CacheDirectory)
                    .Where(f => DesteklenenUzantilar.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f)
                    .ToArray();

                return files;
            }
            catch (Exception ex)
            {
                Logger.LogError("Önbellek dosyaları listelenirken hata oluştu.", ex);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Önbellekteki geçerli görsel dosya sayısını döndürür.
        /// </summary>
        public static int GetCachedCount()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory)) return 0;
                return Directory.GetFiles(CacheDirectory)
                    .Count(f => DesteklenenUzantilar.Contains(Path.GetExtension(f)));
            }
            catch
            {
                return 0;
            }
        }

        private static string? ResolveCachePath(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            string clean = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(clean) || !string.Equals(clean, fileName, StringComparison.Ordinal)) return null;
            if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (!DesteklenenUzantilar.Contains(Path.GetExtension(clean))) return null;

            string full = Path.GetFullPath(Path.Combine(CacheDirectory, clean));
            string root = Path.GetFullPath(CacheDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static string GuvenliDosyaAdiUret(string? fileName, byte[] data)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string cleanName = Path.GetFileName(fileName);
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    cleanName = cleanName.Replace(c, '_');
                }

                string ext = Path.GetExtension(cleanName);
                if (DesteklenenUzantilar.Contains(ext))
                {
                    return cleanName;
                }
            }

            // Dosya adı yoksa veya uzantısı desteklenmiyorsa verinin hash'ini kullan
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(data);
            var sb = new StringBuilder();
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2"));
            }
            return $"cache_{sb}.jpg";
        }

        private static void EskiOnbellekDosyalariniTemizle()
        {
            Task.Run(() =>
            {
                lock (_lockObj)
                {
                    try
                    {
                        if (!Directory.Exists(CacheDirectory)) return;

                        var dirInfo = new DirectoryInfo(CacheDirectory);
                        var files = dirInfo.GetFiles()
                            .Where(f => DesteklenenUzantilar.Contains(f.Extension))
                            .OrderByDescending(f => f.LastWriteTime)
                            .ToList();

                        if (files.Count > MaxOnbellekDosyaSayisi)
                        {
                            var silinecekler = files.Skip(MaxOnbellekDosyaSayisi);
                            foreach (var f in silinecekler)
                            {
                                try
                                {
                                    f.Delete();
                                }
                                catch
                                {
                                    // Dosya kullanımda ise es geç
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Eski önbellek temizleme hatası", ex);
                    }
                }
            });
        }

        // Logo önbellek dizininin dışında tutulur; aksi halde senkronizasyon temizliği onu siler ve slayt olarak oynatılır
        public static readonly string WatermarkCachePath = InitializeWatermarkPath();

        private static string InitializeWatermarkPath()
        {
            string parentDir = Path.GetDirectoryName(CacheDirectory.TrimEnd('\\', '/')) ?? CacheDirectory;
            string newPath = Path.Combine(parentDir, "watermark_cached.png");
            try
            {
                string legacyPath = Path.Combine(CacheDirectory, "watermark_cached.png");
                if (!string.Equals(legacyPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
                {
                    if (!File.Exists(newPath)) File.Move(legacyPath, newPath);
                    else File.Delete(legacyPath);
                }
            }
            catch { }
            return newPath;
        }

        public static async Task SaveWatermarkImageAsync(string? base64Data)
        {
            if (string.IsNullOrWhiteSpace(base64Data))
                return;

            try
            {
                int commaIdx = base64Data.IndexOf(',');
                string cleanBase64 = (commaIdx >= 0) ? base64Data.Substring(commaIdx + 1) : base64Data;
                byte[] imageBytes = Convert.FromBase64String(cleanBase64.Trim());

                await File.WriteAllBytesAsync(WatermarkCachePath, imageBytes);
                Logger.LogInfo("Marka logosu yerel önbelleğe kaydedildi.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Marka logosu yerel önbelleğe kaydedilirken hata oluştu.", ex);
            }
        }

        public static BitmapImage? LoadWatermarkBitmap()
        {
            try
            {
                if (File.Exists(WatermarkCachePath))
                {
                    return ImageHelper.LoadBitmapFromFile(WatermarkCachePath, null);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Önbellekten marka logosu yüklenirken hata oluştu.", ex);
            }
            return null;
        }

        public static void DeleteWatermarkImage()
        {
            try
            {
                if (File.Exists(WatermarkCachePath))
                {
                    File.Delete(WatermarkCachePath);
                    Logger.LogInfo("Önbellekteki marka logosu silindi.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Önbellekteki marka logosu silinirken hata oluştu.", ex);
            }
        }
    }
}
