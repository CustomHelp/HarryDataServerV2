using System;
using System.Threading;
using System.Windows.Threading;

namespace HarryShared.Splash
{
    /// <summary>
    /// Hosts the animated <see cref="SplashWindow"/> on its own dedicated STA UI thread
    /// with its own dispatcher, so the intro animation runs smoothly while the owning
    /// application builds and shows its main window on the main thread.
    ///
    /// Usage in App.OnStartup:
    /// <code>
    /// var splash = SplashHost.Show("DATA SERVER");
    /// try { /* build + Show() main window */ }
    /// finally { splash.Close(); }
    /// </code>
    /// <see cref="Close"/> respects the splash's minimum display time and fades out.
    /// </summary>
    public sealed class SplashHost
    {
        // Preset for the lightweight companion tools: the whole intro plays ~1.75× faster
        // and the splash may fade out after ~2.8s (vs. the server's full 4.5s).
        private const double FastSpeedRatio = 1.75;
        private static readonly TimeSpan FastMinDisplay = TimeSpan.FromSeconds(2.8);

        private SplashWindow? _splash;
        private Thread? _thread;

        private SplashHost() { }

        /// <summary>
        /// Starts the splash on a background STA UI thread and returns once the window
        /// is visible (or after a short safety timeout). Never throws — a splash failure
        /// must never block application startup. Uses the full-length intro (4.5s),
        /// suited to the main server with its heavier startup.
        /// </summary>
        /// <param name="productName">Software name for the product line, e.g. "DATA SERVER".</param>
        public static SplashHost Show(string productName)
            => Create(productName, 1.0, null);

        /// <summary>
        /// Like <see cref="Show"/> but with a faster, shorter intro (~2.8s) for the
        /// lightweight companion tools, which are ready almost instantly.
        /// </summary>
        /// <param name="productName">Software name for the product line, e.g. "GRAPH".</param>
        public static SplashHost ShowFast(string productName)
            => Create(productName, FastSpeedRatio, FastMinDisplay);

        private static SplashHost Create(string productName, double speedRatio, TimeSpan? minDisplay)
        {
            var host = new SplashHost();
            host.Start(productName, speedRatio, minDisplay);
            return host;
        }

        private void Start(string productName, double speedRatio, TimeSpan? minDisplay)
        {
            var ready = new ManualResetEvent(false);

            _thread = new Thread(() =>
            {
                try
                {
                    _splash = new SplashWindow(productName, speedRatio, minDisplay);
                    _splash.Show();
                    ready.Set();
                    // Own message loop -> animation runs independently of the main thread.
                    Dispatcher.Run();
                }
                catch
                {
                    // A splash is purely cosmetic; swallow so it can never crash startup.
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "SplashUIThread"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // Wait until the splash is visible (max 2s as a safety net).
            ready.WaitOne(2000);
        }

        /// <summary>
        /// Fades the splash out and ends its UI thread. Thread-safe (the window dispatches
        /// the close onto its own dispatcher). Waits for the minimum display time first.
        /// </summary>
        public void Close()
        {
            try
            {
                _splash?.BeginClose();
                _splash = null;
            }
            catch
            {
                // Best effort — a failed close must not affect the running application.
            }
        }
    }
}
