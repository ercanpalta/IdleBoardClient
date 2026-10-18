# IdleBoard Client

.NET 8 ve WPF tabanlı, IdleBoard sunucusundan Server-Sent Events (SSE) protokolüyle gerçek zamanlı görsel yayını alan ve tam ekran modunda gösteren Windows istemci uygulaması.

## 🚀 Özellikler

- **Tam Ekran Pano Modu:** Ekranı kaplayan, dikkat dağıtmayan kesintisiz bilgi ve görsel panosu.
- **Gerçek Zamanlı SSE Akışı:** Sunucudan anlık olarak yayınlanan görselleri dinleme ve ekrana yansıtma.
- **Çevrimdışı (Offline) Önbellekleme & Fallback:** Sunucu veya ağ bağlantısı kopsa dahi önbellekteki görsellerle döngüsel slayt gösterisine devam edebilme.
- **Sistem Tepsisi (Tray) Desteği:** İstenildiğinde sistem tepsisine küçülme ve arka planda çalışabilme.
- **Yönetici Ayarları:** `Ctrl + Shift + S` kısayoluyla erişilebilen sunucu bağlantısı ve ayar penceresi.
- **Otomatik Başlatma (AutoStart):** Windows açılışında otomatik başlatılabilme desteği.

## 📦 Gereksinimler ve Derleme

- Windows 10 / 11
- .NET 8.0 SDK / Visual Studio 2022+

```bash
# Projeyi derleyin
dotnet build

# Uygulamayı çalıştırın
dotnet run --project IdleBoardClient
```