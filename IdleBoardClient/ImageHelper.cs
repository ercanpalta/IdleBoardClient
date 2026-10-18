using System;
using System.IO;
using System.Windows.Media.Imaging;

public static class ImageHelper
{
    public static BitmapImage? Base64ToBitmapImage(string? base64String, int? decodePixelWidth = 1920)
    {
        if (string.IsNullOrWhiteSpace(base64String))
            return null;

        try
        {
            // Data URI ("data:image/...;base64,") ön ekini temizle
            int commaIdx = base64String.IndexOf(',');
            string cleanBase64 = (commaIdx >= 0) ? base64String.Substring(commaIdx + 1) : base64String;

            // 1. Base64 dizesini byte dizisine çevir
            byte[] imageBytes = Convert.FromBase64String(cleanBase64.Trim());

            // 2. MemoryStream kullanarak bellekte okuma akışı aç
            using (MemoryStream ms = new MemoryStream(imageBytes))
            {
                BitmapImage bitmap = new BitmapImage();

                bitmap.BeginInit();

                // KRİTİK 1: Akış kapandıktan sonra verinin bellekte kalması için
                // görsel verisini anında belleğe kopyalar (Stream ile bağını keser).
                bitmap.CacheOption = BitmapCacheOption.OnLoad;

                if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
                {
                    bitmap.DecodePixelWidth = decodePixelWidth.Value;
                }

                bitmap.StreamSource = ms;
                bitmap.EndInit();

                // KRİTİK 2: Nesneyi dondur (Freeze).
                bitmap.Freeze();

                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Base64 görseli BitmapImage nesnesine dönüştürülürken hata oluştu.", ex);
            return null;
        }
    }

    /// <summary>
    /// Disk üzerindeki dosyayı dosya kilitlemesi (file lock) oluşturmadan belleğe yükler ve dondurur.
    /// </summary>
    public static BitmapImage? LoadBitmapFromFile(string? filePath, int? decodePixelWidth = 1920)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            byte[] imageBytes = File.ReadAllBytes(filePath);
            using (MemoryStream ms = new MemoryStream(imageBytes))
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
                {
                    bitmap.DecodePixelWidth = decodePixelWidth.Value;
                }
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Önbellekten görsel okunamadı: {filePath}", ex);
            return null;
        }
    }
}