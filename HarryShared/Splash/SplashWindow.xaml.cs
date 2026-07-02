using System;
using System.Linq;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HarryShared.Splash
{
    /// <summary>
    /// Animated HarrySuite splashscreen (laser contour -> logo -> text -> loading bar).
    /// Runs on its own UI thread (see <see cref="SplashHost"/>) so the animation stays
    /// smooth while the owning application builds its (heavier) main window.
    /// The company line is fixed to "CUSTOMHELP"; the product line is passed per app.
    /// </summary>
    public partial class SplashWindow : Window
    {
        // Minimum display time so the intro animation always plays through, even when
        // the main window is ready very quickly. Both configurable per application, so the
        // lightweight companion tools can use a faster/shorter intro than the server.
        private readonly TimeSpan _minDisplay;
        private readonly double _speedRatio;
        private readonly DateTime shownAt = DateTime.Now;
        private bool closing;

        /// <summary>
        /// Creates the splash for one application.
        /// </summary>
        /// <param name="productName">
        /// Software name shown on the turquoise product line, e.g. "DATA SERVER".
        /// It is rendered with wide letter-spacing (words separated by a larger gap).
        /// </param>
        /// <param name="speedRatio">
        /// Animation speed multiplier (1.0 = original LOET timing, &gt;1 = faster/shorter).
        /// </param>
        /// <param name="minDisplay">
        /// Minimum time the splash stays up before it may fade out. Defaults to 4.5s.
        /// </param>
        public SplashWindow(string productName, double speedRatio = 1.0, TimeSpan? minDisplay = null)
        {
            InitializeComponent();
            _speedRatio = speedRatio > 0 ? speedRatio : 1.0;
            _minDisplay = minDisplay ?? TimeSpan.FromSeconds(4.5);
            TxtProduct.Text = SpaceOut(productName);

            Loaded += (_, _) => StartIntro();
        }

        /// <summary>Starts the intro storyboard, scaled by <see cref="_speedRatio"/>.</summary>
        private void StartIntro()
        {
            try
            {
                var sb = (Storyboard)Resources["IntroStoryboard"];
                if (Math.Abs(_speedRatio - 1.0) > 0.001)
                    sb.SpeedRatio = _speedRatio;
                sb.Begin(this, isControllable: true);
            }
            catch
            {
                // The intro animation is purely cosmetic; never let it break startup.
            }
        }

        /// <summary>
        /// Emulates letter-spacing the same way the company line does: each letter of a
        /// word separated by a single space, words separated by a wider gap.
        /// "DATA SERVER" -> "D A T A   S E R V E R".
        /// </summary>
        private static string SpaceOut(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var words = text.Trim()
                            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(w => string.Join(" ", w.ToCharArray()));
            return string.Join("   ", words).ToUpperInvariant();
        }

        /// <summary>
        /// Fades the splash out and then shuts down its own UI thread. May be called
        /// from any thread. Waits until the minimum display time before fading out.
        /// </summary>
        public void BeginClose()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(BeginClose));
                return;
            }

            if (closing) return;
            closing = true;

            TimeSpan remaining = _minDisplay - (DateTime.Now - shownAt);
            if (remaining > TimeSpan.Zero)
            {
                var wait = new DispatcherTimer { Interval = remaining };
                wait.Tick += (s, e) =>
                {
                    wait.Stop();
                    FadeOutAndClose();
                };
                wait.Start();
                return;
            }

            FadeOutAndClose();
        }

        private void FadeOutAndClose()
        {
            var fadeOut = new DoubleAnimation
            {
                From = Opacity,
                To = 0.0,
                Duration = TimeSpan.FromSeconds(0.5)
            };
            fadeOut.Completed += (s, e) =>
            {
                try
                {
                    Close();
                }
                finally
                {
                    // Cleanly end the splash's own dispatcher/thread.
                    Dispatcher.InvokeShutdown();
                }
            };
            BeginAnimation(OpacityProperty, fadeOut);
        }
    }
}
