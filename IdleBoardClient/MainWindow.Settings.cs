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
        private void UygulaSlideInterval(double saniye)
        {
            if (saniye < MinSlideIntervalSaniye || saniye > MaxSlideIntervalSaniye)
                return;

            double yuvarlak = Math.Round(saniye);
            _slideIntervalSaniye = yuvarlak;
            var yeniInterval = TimeSpan.FromSeconds(yuvarlak);

            void Uygula()
            {
                if (_slideTimer.Interval == yeniInterval)
                    return;

                _slideTimer.Interval = yeniInterval;
                if (_isOffline && !_isStreamPinned && !_isStreamStopped)
                {
                    _slideTimer.Stop();
                    _slideTimer.Start();
                }
                EkranaYazdir($"Slayt süresi güncellendi: {yeniInterval.TotalSeconds:0}sn");
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaBostaKalmaZamanAsimi(int saniye)
        {
            if (saniye < 1 || saniye > 86400)
                return;

            if (IdleSuresiSaniye == saniye)
                return;

            IdleSuresiSaniye = saniye;
            Settings.Default.IdleTimeSeconds = saniye;
            Settings.Default.Save();

            Logger.LogInfo($"Sunucudan boşta kalma süresi güncellendi: {saniye} saniye");
            EkranaYazdir($"Boşta kalma süresi güncellendi: {saniye}sn");
        }

        private void UygulaImageFit(string? fitMode)
        {
            if (string.IsNullOrWhiteSpace(fitMode))
                return;

            Stretch stretch;
            switch (fitMode.Trim().ToLowerInvariant())
            {
                case "fill":
                    stretch = Stretch.Fill;
                    break;
                case "uniformtofill":
                case "cover":
                    stretch = Stretch.UniformToFill;
                    break;
                case "none":
                    stretch = Stretch.None;
                    break;
                case "uniform":
                case "contain":
                default:
                    stretch = Stretch.Uniform;
                    break;
            }

            void Uygula()
            {
                if (imgCanli.Stretch != stretch || mediaVideo.Stretch != stretch)
                {
                    imgCanli.Stretch = stretch;
                    mediaVideo.Stretch = stretch;
                    Settings.Default.ImageFit = stretch.ToString();
                    Settings.Default.Save();

                    Logger.LogInfo($"Görsel ve video yerleşimi güncellendi: {stretch}");
                    EkranaYazdir($"Görsel yerleşimi: {stretch}");
                }
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaShowInfoBar(bool visible)
        {
            void Uygula()
            {
                var targetVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (BottomBanner.Visibility != targetVisibility)
                {
                    BottomBanner.Visibility = targetVisibility;
                    Settings.Default.ShowInfoBar = visible;
                    Settings.Default.Save();

                    Logger.LogInfo($"Alt bilgi şeridi güncellendi: {(visible ? "Göster" : "Gizle")}");
                    EkranaYazdir($"Alt bilgi şeridi: {(visible ? "Göster" : "Gizle")}");
                }
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private Image? WatermarkLogo => (Image?)FindName("ImgWatermark");

        private void UygulaWatermarkImage(string? base64)
        {
            void Uygula()
            {
                var logo = WatermarkLogo;
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    var bmp = ImageHelper.Base64ToBitmapImage(base64, null);
                    if (bmp != null)
                    {
                        if (logo != null)
                        {
                            logo.Source = bmp;
                        }
                        _ = ImageCacheManager.SaveWatermarkImageAsync(base64);
                        Logger.LogInfo("Marka logosu güncellendi.");
                    }
                }
                else
                {
                    if (logo != null)
                    {
                        logo.Source = null;
                    }
                    ImageCacheManager.DeleteWatermarkImage();
                    Logger.LogInfo("Marka logosu temizlendi.");
                }
                GuncelleWatermarkGorunurluk();
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void UygulaShowWatermark(bool visible)
        {
            void Uygula()
            {
                _showWatermark = visible;
                if (Settings.Default.ShowWatermark != visible)
                {
                    Settings.Default.ShowWatermark = visible;
                    Settings.Default.Save();
                }
                GuncelleWatermarkGorunurluk();
            }

            if (Dispatcher.CheckAccess())
                Uygula();
            else
                Dispatcher.BeginInvoke(Uygula);
        }

        private void GuncelleWatermarkGorunurluk()
        {
            var logo = WatermarkLogo;
            bool gorunur = _showWatermark && logo != null && logo.Source != null;
            var targetPanel = WatermarkPanel ?? (Border?)FindName("WatermarkPanel");
            if (targetPanel != null)
            {
                var hedef = gorunur ? Visibility.Visible : Visibility.Collapsed;
                if (targetPanel.Visibility != hedef)
                {
                    targetPanel.Visibility = hedef;
                    Logger.LogInfo($"Marka logosu görünürlüğü güncellendi: {(gorunur ? "Göster" : "Gizle")}");
                    EkranaYazdir($"Filigran: {(gorunur ? "Göster" : "Gizle")}");
                }
            }
        }

        private void UygulaTransitionEffect(string? effect)
        {
            if (string.IsNullOrWhiteSpace(effect))
                return;

            string normalized = (effect.Trim().ToLowerInvariant() == "none") ? "none" : "fade";
            if (_transitionEffect == normalized)
                return;

            _transitionEffect = normalized;
            Settings.Default.TransitionEffect = normalized;
            Settings.Default.Save();

            Logger.LogInfo($"Geçiş efekti güncellendi: {normalized}");
            EkranaYazdir($"Geçiş efekti: {normalized}");
        }

        private void UygulaSlideshowOrder(string? order)
        {
            if (string.IsNullOrWhiteSpace(order))
                return;

            string normalized = (order.Trim().ToLowerInvariant() == "random") ? "random" : "sequential";
            if (_slideshowOrder == normalized)
                return;

            _slideshowOrder = normalized;
            Settings.Default.SlideshowOrder = normalized;
            Settings.Default.Save();

            Logger.LogInfo($"Slayt sıralaması güncellendi: {normalized}");
            EkranaYazdir($"Slayt sıralaması: {normalized}");
        }

        private TimeSpan FadeSuresiAl()
        {
            double yariMs = _slideIntervalSaniye * 1000 * 0.2;
            double ms = Math.Clamp(yariMs, 120, 700);
            return TimeSpan.FromMilliseconds(ms);
        }
    }
}
