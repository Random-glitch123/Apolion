using System;
using System.Windows;
using System.Windows.Threading;

namespace Apolion
{
    /// <summary>
    /// Safe bridge from CEF/native threads to the WPF UI thread.
    /// Always asynchronous: using Dispatcher.Invoke from a CEF callback can
    /// deadlock when the UI thread is waiting for CEF at the same time.
    /// </summary>
    internal static class UiDispatch
    {
        public static void Post(Action action, string context)
        {
            Post(System.Windows.Application.Current?.Dispatcher, action, context);
        }

        public static void Post(Dispatcher? dispatcher, Action action, string context)
        {
            if (dispatcher == null) return;

            try
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

                if (dispatcher.CheckAccess())
                {
                    Run(action, context);
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(() => Run(action, context)), DispatcherPriority.Normal);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception(context, ex);
            }
        }

        private static void Run(Action action, string context)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLogger.Exception(context, ex);
            }
        }
    }
}

