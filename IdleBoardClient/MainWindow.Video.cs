using IdleBoardClient;
using System;
using System.Globalization;
using System.Windows.Controls;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
// Forms ve WPF Isim Çakışmasını Önlemek İçin Alias
using Forms = System.Windows.Forms;
using Image = System.Windows.Controls.Image;
using Border = System.Windows.Controls.Border;

namespace SseWpfApp
{
    public partial class MainWindow : Window
    {
        private int _videoPlayVersion = 0;
        private int _videoOpenVersion = 0;

        private async void VideoyuGuncelle(string dosyaAdi, double startOffsetSec = 0)
        {
            try
            {
                int version = Interlocked.Increment(ref _videoPlayVersion);
                _videoStartOffsetSec = Math.Max(0, startOffsetSec);

                // 1. Videonun yerel diskte olduğundan emin ol (Kesinlikle canlı ağdan akış yapılmaz!)
                string? localPath = await ImageCacheManager.EnsureFileDownloadedAsync(ServerUrl, dosyaAdi);

                // Eğer beklerken başka bir video veya slayta geçildiyse işlemi durdur
                if (version != _videoPlayVersion) return;

                if (string.IsNullOrEmpty(localPath) || !System.IO.File.Exists(localPath))
                {
                    Logger.LogError($"[Video Oynatıcı] Video yerel diske indirilemedi: {dosyaAdi}. Siyah ekran verilmiyor, mevcut görsel korunuyor.");
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (version != _videoPlayVersion) return;

                    var videoUri = new Uri(localPath, UriKind.Absolute);

                    if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Collapsed;
                    mediaVideo.Stop();
                    _videoOpenVersion = version;
                    mediaVideo.Source = videoUri;
                    mediaVideo.Volume = 0;
                    mediaVideo.IsMuted = true;
                    mediaVideo.Play();

                    Logger.LogInfo($"[Video Oynatıcı] Video yerel diskten oynatılıyor: {dosyaAdi}");
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Video Oynatıcı] Video yerel diskten başlatılırken hata: {dosyaAdi}", ex);
            }
        }

        private void MediaVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            int acilanSurum = _videoOpenVersion;
            Interlocked.Exchange(ref _videoFailStreak, 0);
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    // Açılış beklenirken daha yeni bir resim/video geldiyse eski videonun ekranı ezmesini engelle
                    if (acilanSurum != _videoPlayVersion)
                    {
                        mediaVideo.Stop();
                        return;
                    }

                    mediaVideo.Visibility = Visibility.Visible;
                    mediaVideo.Opacity = 1.0;

                    if (_videoStartOffsetSec > 0.5)
                    {
                        mediaVideo.Position = TimeSpan.FromSeconds(_videoStartOffsetSec);
                        _videoStartOffsetSec = 0;
                    }

                    mediaVideo.Play();

                    // Video açılıp ilk kare hazır olunca önceki görseli temizle (artık bellekte veya ekranda eski resim kalmaz)
                    imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                    imgCanli.Opacity = 0.0;
                    imgCanli.Source = null;
                    imgCanli.Visibility = Visibility.Collapsed;
                }
                catch { }
            }));
        }

        private void MediaVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isStreamPinned)
                {
                    // Sabitlenmiş videoyu baştan döngüye al
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        mediaVideo.Position = TimeSpan.Zero;
                        mediaVideo.Play();
                    }));
                    return;
                }

                mediaVideo.Pause();
                if (_isOffline)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        SonrakiOfflineGorseliniGoster();
                    }));
                }
            }
            catch { }
        }

        private int _videoFailStreak = 0;

        private async void MediaVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            Logger.LogError($"[Video Oynatıcı] Video yerel diskten oynatılamadı: {e.ErrorException?.Message}", e.ErrorException);
            int streak = Interlocked.Increment(ref _videoFailStreak);
            int surum = _videoPlayVersion;
            mediaVideo.Visibility = Visibility.Collapsed;
            mediaVideo.Source = null;

            // Bozuk dosyada CPU'yu yormamak için art arda hatalarda sunucunun sonraki slaytı beklenir
            if (streak > 3) return;
            await Task.Delay(1000);
            if (surum != _videoPlayVersion) return;

            // Asla siyah ekranda bırakma, yerel diskteki bir sonraki görseli göster
            SonrakiOfflineGorseliniGoster();
        }
    }
}
