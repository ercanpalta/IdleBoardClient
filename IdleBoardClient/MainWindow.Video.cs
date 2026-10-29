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
        private DispatcherTimer? _videoLoopWatchdog;
        private double _aktifVideoSuresiSn = 0;
        private int _videoOpenVersion = 0;
        private static bool _mfChecked = false;
        private static bool _mfAvailable = true;

        public static bool IsMediaFoundationAvailable()
        {
            if (_mfChecked) return _mfAvailable;
            try
            {
                IntPtr hMod = NativeMethods.LoadLibrary("mfplat.dll");
                if (hMod != IntPtr.Zero)
                {
                    NativeMethods.FreeLibrary(hMod);
                    _mfAvailable = true;
                }
                else
                {
                    _mfAvailable = false;
                    Logger.LogError("[Windows Medya Uyarısı] mfplat.dll yüklenemedi. Windows N/KN sürümlerinde video oynatabilmek için Microsoft Media Feature Pack kurulmalıdır.");
                }
            }
            catch (Exception ex)
            {
                _mfAvailable = false;
                Logger.LogError("[Windows Medya Uyarısı] Media Foundation denetim hatası", ex);
            }
            _mfChecked = true;
            return _mfAvailable;
        }

        private async void VideoyuGuncelle(string dosyaAdi, double startOffsetSec = 0)
        {
            try
            {
                if (!IsMediaFoundationAvailable())
                {
                    Logger.LogInfo($"[Video Oynatıcı] Media Foundation bileşenleri eksik olduğu için video atlanıyor: {dosyaAdi}");
                    return;
                }

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
                _aktifVideoSuresiSn = Math.Max(0.5, ImageCacheManager.GetVideoDuration(localPath));

                Dispatcher.Invoke(() =>
                {
                    if (version != _videoPlayVersion) return;

                    var videoUri = new Uri(localPath, UriKind.Absolute);

                    if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Collapsed;

                    // Eğer ekrandaki video ile aynı video tekrar geldiyse (tek video döngüsü veya ardışık yayın):
                    // Kaynağı sıfırdan atayıp MediaOpened beklemeden anında başa sar ve oynat (kesintisiz loop)
                    if (Equals(mediaVideo.Source, videoUri))
                    {
                        if (_videoStartOffsetSec > 0.05)
                        {
                            mediaVideo.Position = TimeSpan.FromSeconds(_videoStartOffsetSec);
                        }
                        else
                        {
                            mediaVideo.Position = TimeSpan.Zero;
                        }
                        mediaVideo.Visibility = Visibility.Visible;
                        mediaVideo.Opacity = 1.0;
                        mediaVideo.Play();
                        EnsureVideoWatchdogRunning();

                        imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                        imgCanli.Opacity = 0.0;
                        imgCanli.Source = null;
                        imgCanli.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        mediaVideo.Stop();
                        _videoOpenVersion = version;
                        mediaVideo.Source = videoUri;
                        mediaVideo.Volume = 0;
                        mediaVideo.IsMuted = false;
                        mediaVideo.Play();
                    }

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

                    if (mediaVideo.NaturalDuration.HasTimeSpan && mediaVideo.NaturalDuration.TimeSpan.TotalSeconds > 0.5)
                    {
                        _aktifVideoSuresiSn = mediaVideo.NaturalDuration.TimeSpan.TotalSeconds;
                    }

                    if (_videoStartOffsetSec > 0.5)
                    {
                        mediaVideo.Position = TimeSpan.FromSeconds(_videoStartOffsetSec);
                        _videoStartOffsetSec = 0;
                    }

                    mediaVideo.Play();
                    EnsureVideoWatchdogRunning();

                    // Çevrimdışı slaytta zamanlayıcıyı sabit 65 sn yerine videonun gerçek süresine göre ayarla
                    if (_isOffline && _slideTimer.IsEnabled && mediaVideo.NaturalDuration.HasTimeSpan)
                    {
                        double kalanSn = mediaVideo.NaturalDuration.TimeSpan.TotalSeconds - mediaVideo.Position.TotalSeconds;
                        _slideTimer.Stop();
                        _slideTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, kalanSn + 1));
                        _slideTimer.Start();
                    }

                    // Video açılıp ilk kare hazır olunca önceki görseli temizle (artık bellekte veya ekranda eski resim kalmaz)
                    imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                    imgCanli.Opacity = 0.0;
                    imgCanli.Source = null;
                    imgCanli.Visibility = Visibility.Collapsed;
                }
                catch { }
            }));
        }

        private void EnsureVideoWatchdogRunning()
        {
            if (_videoLoopWatchdog == null)
            {
                _videoLoopWatchdog = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _videoLoopWatchdog.Tick += VideoLoopWatchdog_Tick;
            }
            if (!_videoLoopWatchdog.IsEnabled)
            {
                _videoLoopWatchdog.Start();
            }
        }

        private void VideoLoopWatchdog_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (mediaVideo.Visibility != Visibility.Visible || mediaVideo.Source == null)
                {
                    _videoLoopWatchdog?.Stop();
                    return;
                }

                double durationSec = _aktifVideoSuresiSn;
                if (mediaVideo.NaturalDuration.HasTimeSpan && mediaVideo.NaturalDuration.TimeSpan.TotalSeconds > 0.5)
                {
                    durationSec = mediaVideo.NaturalDuration.TimeSpan.TotalSeconds;
                }

                if (durationSec <= 0.5) return;

                // Windows Media Foundation EVR MediaEnded olayını tetiklemediğinde
                // videonun bittiğini konumuyla kesin olarak tespit et ve baştan döngüye al
                if (mediaVideo.Position.TotalSeconds >= (durationSec - 0.15))
                {
                    if (_isStreamPinned)
                    {
                        mediaVideo.Position = TimeSpan.Zero;
                        mediaVideo.Play();
                        Logger.LogInfo($"[Pin] Video sona ulaştı ({durationSec:0.0}s), baştan döngüye alındı.");
                    }
                    else if (_isOffline)
                    {
                        _videoLoopWatchdog?.Stop();
                        mediaVideo.Pause();
                        SonrakiOfflineGorseliniGoster();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[Video Watchdog] Hata:", ex);
            }
        }

        private void MediaVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isStreamPinned)
                {
                    // Sabitlenmiş videoyu baştan döngüye al (sürekli tekrar tekrar oynat)
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        try
                        {
                            if (!_isStreamPinned) return;
                            mediaVideo.Position = TimeSpan.Zero;
                            mediaVideo.Play();
                            Logger.LogInfo("[Pin] MediaEnded: Sabitlenmiş video baştan döngüye alındı.");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("[Pin] Sabitlenmiş video baştan başlatılırken hata:", ex);
                        }
                    }));
                    return;
                }

                mediaVideo.Pause();
                if (_isOffline)
                {
                    _videoLoopWatchdog?.Stop();
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        SonrakiOfflineGorseliniGoster();
                    }));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[Video Oynatıcı] MediaEnded işlenirken hata:", ex);
            }
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
