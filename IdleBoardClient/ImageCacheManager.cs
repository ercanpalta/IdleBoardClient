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
        public static readonly string CacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

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
        /// Eksik olan yayındaki görseller sunucudan doğrudan HTTP ile indirilip önbelleğe alınır.
        /// </summary>
        public static async Task SyncActiveImagesAsync(string serverBaseUrl, IReadOnlyList<string> activeFileNames, CancellationToken ct = default)
        {
            if (activeFileNames == null) return;

            await Task.Run(async () =>
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

                if (activeFileNames.Count == 0) return;

                // 2. ADIM: Önbellekte eksik olan aktif görselleri HTTP üzerinden hızlıca ön-yükle (Pre-fetch)
                string baseUriString = serverBaseUrl;
                try
                {
                    var uri = new Uri(serverBaseUrl);
                    baseUriString = uri.GetLeftPart(UriPartial.Authority);
                }
                catch { }

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                foreach (var fileName in activeFileNames)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(fileName)) continue;

                    string localPath = Path.Combine(CacheDirectory, fileName);
                    if (File.Exists(localPath))
                    {
                        var fi = new FileInfo(localPath);
                        if (fi.Length > 0)
                        {
                            continue;
                        }
                    }

                    try
                    {
                        string downloadUrl = $"{baseUriString.TrimEnd('/')}/images/{Uri.EscapeDataString(fileName)}";
                        var response = await httpClient.GetAsync(downloadUrl, ct).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                            if (bytes.Length > 0)
                            {
                                string tempPath = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                try
                                {
                                    await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
                                    File.Move(tempPath, localPath, overwrite: true);
                                    Logger.LogInfo($"[Önbellek Ön-Yükleme] Eksik dosya önbelleğe kaydedildi: {fileName} ({bytes.Length / 1024} KB)");
                                }
                                catch (Exception writeEx)
                                {
                                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                                    Logger.LogError($"[Önbellek Ön-Yükleme] Dosya yazılamadı: {fileName}", writeEx);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Logger.LogError($"[Önbellek Ön-Yükleme] Görsel indirilemedi: {fileName}", ex);
                    }
                }
            }, ct).ConfigureAwait(false);
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

        public static readonly string WatermarkCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "watermark_cached.png");

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
