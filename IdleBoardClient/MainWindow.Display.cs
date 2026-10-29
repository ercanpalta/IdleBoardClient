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
        private void ResmiGuncelle(string? base64String)
        {
            if (string.IsNullOrWhiteSpace(base64String)) return;

            var yeniBitmap = ImageHelper.Base64ToBitmapImage(base64String);
            if (yeniBitmap != null)
            {
                ResmiGuncelle(yeniBitmap);
            }
        }

        private void ResmiGuncelle(BitmapImage yeniBitmap)
        {
            // Beklemede olan video açılışları bu görselden daha eski sayılır ve iptal edilir
            Interlocked.Increment(ref _videoPlayVersion);
            // Yüksek frekanslı resim değişimlerinde UI'ı kilitlememek için BeginInvoke kullanıyoruz
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    int surum = ++_resimAnimasyonSurum;
                    TimeSpan fadeSuresi = FadeSuresiAl();
                    TimeSpan doubleFade = TimeSpan.FromMilliseconds(fadeSuresi.TotalMilliseconds * 2);

                    // Her zaman görsel ekranını görünür kıl ve bekleme panelini gizle
                    imgCanli.Visibility = Visibility.Visible;
                    if (StandbyPanel != null) StandbyPanel.Visibility = Visibility.Collapsed;

                    // EĞER VİDEODAN RESME GEÇİLİYORSA:
                    // Eski resmi hiç devreye sokmadan doğrudan yeni resmi videonun dondurulmuş son karesi üstüne açıyoruz!
                    if (mediaVideo.Visibility == Visibility.Visible)
                    {
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Visibility = Visibility.Visible;

                        if (_transitionEffect == "none")
                        {
                            imgCanli.Opacity = 1.0;
                            _videoLoopWatchdog?.Stop();
                            mediaVideo.Stop();
                            mediaVideo.Source = null;
                            mediaVideo.Visibility = Visibility.Collapsed;
                            return;
                        }

                        imgCanli.Opacity = 0.0;
                        var videoFadeIn = new DoubleAnimation(0.0, 1.0, doubleFade)
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        videoFadeIn.Completed += (s, e) =>
                        {
                            if (surum != _resimAnimasyonSurum) return;
                            try
                            {
                                mediaVideo.Stop();
                                mediaVideo.Source = null;
                                mediaVideo.Visibility = Visibility.Collapsed;
                            }
                            catch { }
                        };
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, videoFadeIn);
                        return;
                    }

                    // NORMAL RESİMDEN RESME GEÇİŞ VEYA İLK AÇILIŞ
                    if (_transitionEffect == "none" || imgCanli.Source == null)
                    {
                        imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Opacity = 1.0;
                        return;
                    }

                    // Ensure no lingering animations and start from fully visible
                    imgCanli.BeginAnimation(UIElement.OpacityProperty, null);
                    imgCanli.Opacity = 1.0;

                    // Fade-out from fully visible to fully transparent (double duration)
                    var fadeOut = new DoubleAnimation(1.0, 0.0, doubleFade)
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                    };

                    fadeOut.Completed += (s, e) =>
                    {
                        if (surum != _resimAnimasyonSurum)
                            return;

                        // Swap image source while fully transparent
                        imgCanli.Source = yeniBitmap;
                        imgCanli.Opacity = 0.0;

                        // Fade-in the new image
                        var fadeIn = new DoubleAnimation(0.0, 1.0, doubleFade)
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };

                        imgCanli.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    };

                    imgCanli.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                catch (Exception ex)
                {
                    EkranaYazdir($"[RESİM GÜNCELLEME HATASI] {ex.Message}");
                }
            }));
        }

        private void EkranaYazdir(string mesaj)
        {
            // Arayüzde log alanı olmadığı için metinleri Visual Studio Output penceresine yazdırıyoruz
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {mesaj}");
        }

        public enum BaglantiDurumu
        {
            Bagli,      // Yeşil
            Baglaniyor, // Sarı / Turuncu
            Koptu       // Kırmızı
        }
    }
}
