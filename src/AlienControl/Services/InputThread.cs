using System;
using System.Threading;
using System.Windows.Threading;

namespace AlienControl.Services;

/// <summary>
/// 1.3.0-beta.1 (review #4): the G-key low-level hook and the macro-key raw-input window live on their own thread with
/// their own message loop. A busy UI thread (layout, schtasks, a slide) can no longer delay every keystroke on the
/// system or make Windows silently drop the hook. The thread only pumps messages: ~0% CPU.
/// </summary>
public static class InputThread
{
    private static Dispatcher? _dispatcher;

    public static Dispatcher Dispatcher
    {
        get
        {
            if (_dispatcher is not null) return _dispatcher;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "AlienControl input" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
            return _dispatcher!;
        }
    }

    public static T Invoke<T>(Func<T> f) => Dispatcher.Invoke(f);
    public static void Invoke(Action a) => Dispatcher.Invoke(a);

    public static void Shutdown()
    {
        try { _dispatcher?.InvokeShutdown(); } catch (Exception ex) { Log.Warn("InputThread.Shutdown", ex); }
    }
}
