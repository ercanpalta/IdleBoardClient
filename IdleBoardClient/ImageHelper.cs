using System;
using System.IO;
using System.Windows.Media.Imaging;

public static class ImageHelper
{
    public static BitmapImage? Base64ToBitmapImage(string? base64String)
    {
        if (string.IsNullOrWhiteSpace(base64String))
            return null;

        // 1. Base64 dizesini byte dizisine çevir
        byte[] imageBytes = Convert.FromBase64String(base64String);

        // 2. MemoryStream kullanarak bellekte okuma akışı aç
        using (MemoryStream ms = new MemoryStream(imageBytes))
        {
            BitmapImage bitmap = new BitmapImage();

            bitmap.BeginInit();

            // KRİTİK 1: Akış kapandıktan sonra verinin bellekte kalması için
            // görsel verisini anında belleğe kopyalar (Stream ile bağını keser).
            bitmap.CacheOption = BitmapCacheOption.OnLoad;

            // Eğer gelen görseller çok yüksek çözünürlüklüyse (örn. 4K) ve ekranda 
            // daha küçük gösteriliyorsa bellek tüketimini düşürmek için alt satırı açabilirsiniz:
            bitmap.DecodePixelWidth = 1920; 

            bitmap.StreamSource = ms;
            bitmap.EndInit();

            // KRİTİK 2: Nesneyi dondur (Freeze).
            // Bu işlem nesneyi Read-Only yapar. WPF'in Dependency Property takibini
            // kaldırır, Garbage Collector'ın rahatça silmesini sağlar ve 
            // görselin arka plan thread'inden UI thread'ine aktarılmasına izin verir.
            bitmap.Freeze();

            return bitmap;
        } // MemoryStream burada otomatik Dispose edilir ve bellek serbest kalır.
    }
}