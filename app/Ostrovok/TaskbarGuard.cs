using System.Diagnostics;

namespace Ostrovok;

// A separate process owns the temporary visibility change so a crashed island releases it too.
static class TaskbarGuard
{
    public static void Watch(int pid)
    {
        Process owner;
        try { owner = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (owner)
        {
            using var enabled = EventWaitHandle.OpenExisting("Ostrovok.Flyouts." + pid);
            using var mask = new FlyoutMask();
            var hidden = new Dictionary<IntPtr, string>();
            try
            {
                while (!owner.HasExited)
                {
                    mask.Update(enabled.WaitOne(0));
                    foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                    {
                        IntPtr h = IntPtr.Zero;
                        while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                        {
                            if (!Native.IsWindowVisible(h)) continue;
                            hidden[h] = cls;
                            Native.ShowWindow(h, 0);
                        }
                    }
                    if (owner.WaitForExit(250)) break;
                }
            }
            finally
            {
                foreach (var entry in hidden)
                    if (Native.ClassName(entry.Key) == entry.Value) Native.ShowWindow(entry.Key, 4);
            }
        }
    }
}
