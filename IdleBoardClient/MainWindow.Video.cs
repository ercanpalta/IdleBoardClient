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

        private async void VideoyuGuncelle(string dosyaAdi, double startOffsetSec = 0)
        {
            try
            {
                int version = ++_videoPlayVersion;
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
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
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

        private void MediaVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            Logger.LogError($"[Video Oynatıcı] Video yerel diskten oynatılamadı: {e.ErrorException?.Message}", e.ErrorException);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                mediaVideo.Visibility = Visibility.Collapsed;
                mediaVideo.Source = null;
                // Asla siyah ekranda bırakma, yerel diskteki bir sonraki görseli göster
                SonrakiOfflineGorseliniGoster();
            }));
        }
    }
}
