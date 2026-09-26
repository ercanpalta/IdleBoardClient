using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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
            ".jpg", ".jpeg", ".png", ".webp"
        };

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

                byte[] imageBytes = Convert.FromBase64String(base64Data);
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
    }
}
