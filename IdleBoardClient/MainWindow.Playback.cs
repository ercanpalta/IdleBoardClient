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
        private void SlideTimer_Tick(object? sender, EventArgs e)
        {
            // Sabitlenmiş veya durdurulmuş yayında ya da çevrimiçi modda asla yerel slaytı ilerletme
            if (_isStreamPinned || _isStreamStopped || !_isOffline)
            {
                return;
            }

            // Çevrimdışı modda slayt döngüsünü yerel zamanlayıcı yönetir
            SonrakiOfflineGorseliniGoster();
        }

        private void SetStreamStopped(bool stopped)
        {
            if (_isStreamStopped == stopped) return;
            _isStreamStopped = stopped;

            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (stopped)
                {
                    Logger.LogInfo("Sunucudan yayını durdur komutu alındı. İstemci sistem tepsisine gizlendi.");
                    this.Hide();
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Text = "IdleBoard: Yayın Durduruldu";
                    }
                    if (TxtStatus != null)
                    {
                        TxtStatus.Text = "Yayın Durduruldu (Tepside)";
                    }
                    if (DotStatus != null)
                    {
                        DotStatus.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // Turuncu
                    }
                }
                else
                {
                    Logger.LogInfo("Sunucudan yayını başlat komutu alındı. İstemci ekrana getirildi.");
                    UygulamayiAc();
                    DurumIconGuncelle(BaglantiDurumu.Bagli);
                }
            }));
        }

        private void ClearLiveImage()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                ++_resimAnimasyonSurum;
                if (mediaVideo.Visibility == Visibility.Visible)
                {
                    mediaVideo.Stop();
                    mediaVideo.Source = null;
                    mediaVideo.Visibility = Visibility.Collapsed;
                }
                imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                imgCanli.Source = null;
                imgCanli.Opacity = 1.0;
                imgCanli.Visibility = Visibility.Visible;
                if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Visible;
            }));
        }

        private void EnsureOfflineSlideTimerRunning()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_isOffline && !_isStreamPinned && !_isStreamStopped && !_slideTimer.IsEnabled)
                {
                    _slideTimer.Start();
                }
            }));
        }

        private void SetOfflineMode(bool offline)
        {
            if (_isOffline == offline)
            {
                // _isOffline başlangıçta true olduğundan, ilk bağlantı denemesi başarısız olduğunda da
                // yerel slayt zamanlayıcısının çalıştığından emin ol
                if (offline) EnsureOfflineSlideTimerRunning();
                return;
            }
            _isOffline = offline;

            if (_isOffline)
            {
                Logger.LogInfo("Ağ/Sunucu bağlantısı koptu. Çevrimdışı (offline) önbellek moduna geçildi.");
                EkranaYazdir("[ÇEVRİMDISI MOD] Önbellekteki görsellerle slayt gösterisi devam ediyor.");

                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (!_isStreamPinned && !_isStreamStopped && !_slideTimer.IsEnabled)
                    {
                        _slideTimer.Start();
                    }

                    if (imgCanli.Source == null && ImageCacheManager.GetCachedCount() > 0)
                    {
                        SonrakiOfflineGorseliniGoster();
                    }
                }));
            }
            else
            {
                Logger.LogInfo("Sunucu bağlantısı yeniden sağlandı. Çevrimiçi canlı yayın devralındı.");
                EkranaYazdir("[ÇEVRİMİÇİ MOD] Sunucu yayını aktif.");

                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (_slideTimer.IsEnabled)
                    {
                        _slideTimer.Stop();
                    }
                }));
            }
        }

        private void SonrakiOfflineGorseliniGoster()
        {
            var cachedFiles = ImageCacheManager.GetCachedImageFiles();
            if (cachedFiles.Count == 0)
            {
                EkranaYazdir("[ÇEVRİMDISI] Önbellekte gösterilecek kayıtlı görsel bulunamadı.");
                return;
            }

            if (_slideshowOrder == "random" && cachedFiles.Count > 1)
            {
                int nextIdx;
                do
                {
                    nextIdx = Random.Shared.Next(cachedFiles.Count);
                } while (nextIdx == _offlineImageIndex);
                _offlineImageIndex = nextIdx;
            }
            else
            {
                if (_offlineImageIndex >= cachedFiles.Count)
                {
                    _offlineImageIndex = 0;
                }
            }

            string filePath = cachedFiles[_offlineImageIndex];
            if (_slideshowOrder != "random")
            {
                _offlineImageIndex = (_offlineImageIndex + 1) % cachedFiles.Count;
            }

            if (ImageCacheManager.IsVideoFile(filePath))
            {
                Logger.LogInfo($"[Çevrimdışı Slayt] Video gösteriliyor: {System.IO.Path.GetFileName(filePath)} ({_offlineImageIndex + 1}/{cachedFiles.Count})");
                VideoyuGuncelle(System.IO.Path.GetFileName(filePath));
                if (_slideTimer.IsEnabled)
                {
                    double videoSn = ImageCacheManager.GetVideoDuration(filePath);
                    _slideTimer.Stop();
                    _slideTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, videoSn + 1));
                    _slideTimer.Start();
                }
            }
            else
            {
                if (_slideTimer.Interval != TimeSpan.FromSeconds(_slideIntervalSaniye))
                {
                    _slideTimer.Interval = TimeSpan.FromSeconds(_slideIntervalSaniye);
                }
                var bitmap = ImageHelper.LoadBitmapFromFile(filePath);
                if (bitmap != null)
                {
                    Logger.LogInfo($"[Çevrimdışı Slayt] Görsel gösteriliyor: {System.IO.Path.GetFileName(filePath)} ({_offlineImageIndex + 1}/{cachedFiles.Count})");
                    ResmiGuncelle(bitmap);
                }
            }
        }
    }
}
