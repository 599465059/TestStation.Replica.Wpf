using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace TestStation.Smoke;

internal static class NativePointer
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }

    public static void Keys(params byte[] keys)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var thread = new Thread(() =>
        {
            try { foreach (var key in keys) { keybd_event(key, 0, 0, UIntPtr.Zero); Thread.Sleep(30); } }
            finally
            {
                foreach (var key in keys.Reverse()) { keybd_event(key, 0, 0x0002, UIntPtr.Zero); Thread.Sleep(20); }
                dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            }
        }) { IsBackground = true };
        thread.Start(); Dispatcher.PushFrame(frame); thread.Join();
    }

    public static void Drag(Point source, Point destination, Action? hovering = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            GetCursorPos(out var original);
            try
            {
                SetCursorPos((int)source.X, (int)source.Y);
                Thread.Sleep(100);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(100);
                for (var step = 1; step <= 18; step++)
                {
                    SetCursorPos((int)(source.X + (destination.X - source.X) * step / 18),
                        (int)(source.Y + (destination.Y - source.Y) * step / 18));
                    Thread.Sleep(25);
                }
                Thread.Sleep(100);
                if (hovering is not null) dispatcher.Invoke(hovering);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(120);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(original.X, original.Y);
                dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            }
        }) { IsBackground = true };
        thread.Start();
        Dispatcher.PushFrame(frame);
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public static void Hover(Point destination)
    {
        SetCursorPos((int)destination.X, (int)destination.Y);
    }

    public static void Click(Point point, bool twice = false)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var thread = new Thread(() =>
        {
            GetCursorPos(out var original);
            try
            {
                SetCursorPos((int)point.X, (int)point.Y);
                for (var index = 0; index < (twice ? 2 : 1); index++)
                {
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(45);
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(70);
                }
            }
            finally { SetCursorPos(original.X, original.Y); dispatcher.BeginInvoke(new Action(() => frame.Continue = false)); }
        }) { IsBackground = true };
        thread.Start(); Dispatcher.PushFrame(frame); thread.Join();
    }
}
