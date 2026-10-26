# IdleBoard Client Kurulum Paketi (Installer)

Bu dizin, son kullanıcıların tek tıklamayla sistemi kurabilmesi için hazırlanan Inno Setup yapılandırmasını ve otomatik derleme betiklerini içerir.

## Kurulum Özellikleri ve Kuralları

1. **.NET Bağımsızlığı (Self-Contained):**
   - Uygulama `win-x64` mimarisinde bağımsız (Self-Contained) olarak tek dosya (`IdleBoardClient.exe`) halinde yayımlanır.
   - İstemci bilgisayarında .NET Runtime kurulu olması gerekmez.

2. **Sunucu Adresi Yapılandırması:**
   - Kurulum sihirbazında kullanıcıya sunucu adresi sorulur.
   - Varsayılan kutu içeriği `http://ServerIp:PortNumber` olarak gelir.
   - Girilen adres `client-config.json` dosyasına ve Windows Registry (`HKCU\Software\IdleBoardClient\ServerUrl`) alanına yazılır.

3. **Otomatik Başlatma (Mecburi / Sessiz):**
   - Kurulum ekranında kullanıcıya gösterilmez; arka planda mecburi olarak Windows Başlangıcı'na (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) eklenir.

4. **Güvenlik Duvarı (Firewall) İzni:**
   - Kurulum esnasında Windows Güvenlik Duvarı'na otomatik olarak gelen kuralı (`IdleBoardClient`) eklenir.
   - Kaldırma (Uninstall) işlemi sırasında bu kural otomatik temizlenir.

5. **Kısayol Seçenekleri:**
   - Başlat Menüsü kısayolu varsayılan olarak her zaman oluşturulur.
   - Masaüstü kısayolu seçeneği ise varsayılan olarak işaretlenmemiş (kullanıcının isteğine bağlı) gelir.

6. **Kaldırma (Uninstall) ve Önbellek Temizliği:**
   - Uygulama kaldırıldığında, indirilen önbellek görselleri (`cache/`) ve loglar kullanıcıya sorulmadan sessizce ve tamamen silinir.

7. **Hedef Dizin Seçimi:**
   - Kullanıcı dilerse kurulum dizinini sihirbaz ekranından değiştirebilir (Varsayılan: `C:\Program Files\IdleBoard\Client`).

---

## Yeni Kurulum Paketi Oluşturma (Tek Komut)

Yeni bir kurulum paketi üretmek istediğinizde PowerShell üzerinden aşağıdaki betiği çalıştırmanız yeterlidir:

```powershell
& "IdleBoardClient\installer\build-installer.ps1"
```

Üretilen kurulum dosyası şu konuma kaydedilir:
`IdleBoardClient\installer_output\IdleBoardClient_Setup.exe`
