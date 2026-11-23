using System.Globalization;
using System.Runtime.InteropServices;

namespace Ostrovok;

/// <summary>
/// A low-level keyboard hook that takes Alt+Tab over from Windows: while Alt is held, Tab (and Shift+Tab,
/// the arrow keys) move the selection in the island, releasing Alt switches, Esc cancels.
/// The callback runs on the UI thread and must return at once, so every handler is posted with BeginInvoke.
/// Windows does not let a normal program see keys typed into a window that runs as administrator:
/// there the usual Windows Alt+Tab appears.
/// </summary>
sealed class KeyboardHook : IDisposable
{
    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;
    public const byte VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B,
                      VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    /// <summary>An unassigned key, pressed while Alt is held so the window behind does not open its menu bar
    /// when Alt is released, and Windows does not read Alt+Shift as "switch keyboard layout".</summary>
    const byte VK_MASK = 0xE8;

    readonly Control owner;
    IntPtr hook;
    LowLevelKeyboardProc proc;   // kept in a field: the hook must not lose its delegate to the GC
    bool shift;

    /// <summary>Asked on Alt+Tab: take it over now? (setting on, page ready, not under an exclusive full-screen game)</summary>
    public Func<bool> CanTakeAltTab;
    /// <summary>Asked for the volume and media keys: handle this one in the island? Windows then never sees the key,
    /// so it does not show its own volume / media flyout.</summary>
    public Func<uint, bool> TakeMediaKey;
    public Func<bool> CanSwitchLayout;
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
    bool layoutSpaceHeld, leftAltHeld, layoutChord;
    byte withheldShift;
    static readonly UIntPtr replayTag = new(0x4F535452u);
    static void Replay(byte key, byte scan, uint flags) => Native.keybd_event(key, scan, flags, replayTag);
    public bool Active { get; private set; }
    public event Action<int> Started, Stepped;   // +1 forward, -1 back
    public event Action Committed, Cancelled, CapsPressed;
    public event Action<uint> MediaKey;

    public const uint VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF,
                      VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;
    readonly HashSet<uint> takenDown = new();

    public KeyboardHook(Control owner) => this.owner = owner;

    public void Install()
    {
        proc = Proc;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Write("keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    /// <summary>The page picked a window with the mouse: the session is over, the coming Alt release does nothing.</summary>
    public void End() => Active = false;

    static void Mask()
    {
        Native.keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_MASK, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    void Post(Action a) { if (owner.IsHandleCreated) owner.BeginInvoke(a); }

    static bool SwitchLayout(bool backwards)
    {
        var target = KeyboardLayout.FocusedWindow(Native.GetForegroundWindow());
        var layouts = new IntPtr[GetKeyboardLayoutList(0, null)];
        int count = GetKeyboardLayoutList(layouts.Length, layouts);
        var thread = Native.GetWindowThreadProcessId(target, out _);
        var current = Native.GetKeyboardLayout(thread);
        int index = Array.IndexOf(layouts, current);
        if (index < 0)
            index = Array.FindIndex(layouts, layout => ((long)layout & 0xffff) == ((long)current & 0xffff));
        return target != IntPtr.Zero && count > 0 &&
            Native.PostMessage(target, 0x0050, IntPtr.Zero, layouts[index < 0 ? 0 : (index + (backwards ? count - 1 : 1)) % count]);
    }

    IntPtr Proc(int n, IntPtr w, IntPtr l)
    {
        if (n < 0) return CallNextHookEx(hook, n, w, l);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            int msg = (int)w;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
            uint vk = k.vkCode;
            // Remapping software and on-screen keyboards can synthesize Win+Space.
            if (k.extra == (IntPtr)(long)replayTag.ToUInt64() ||
                ((k.flags & LLKHF_INJECTED) != 0 && vk is not (0x20 or VK_LSHIFT or VK_RSHIFT or VK_LMENU or VK_TAB)))
                return CallNextHookEx(hook, n, w, l);
            if (vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT) shift = down;
            if (vk == VK_LMENU) leftAltHeld = down;
            if (withheldShift != 0)
            {
                if (vk == withheldShift)
                {
                    if (up)
                    {
                        if (layoutChord) SwitchLayout(false);
                        withheldShift = 0;
                        layoutChord = false;
                    }
                    return 1;
                }
                if (vk == VK_LMENU && up && layoutChord)
                {
                    SwitchLayout(false);
                    layoutChord = false;
                    Mask();
                    Replay(withheldShift, 0, 0);
                    withheldShift = 0;
                }
                else if (down && vk != VK_LMENU)
                {
                    layoutChord = false;
                    if (vk != VK_TAB || CanTakeAltTab?.Invoke() != true)
                    {
                        Replay(withheldShift, 0, 0);
                        withheldShift = 0;
                        Replay((byte)vk, (byte)k.scanCode, k.flags & 1);
                        return 1;
                    }
                }
            }
            else if (down && vk is (VK_LSHIFT or VK_RSHIFT) && leftAltHeld && !Active &&
                     (Native.GetAsyncKeyState(0x11) & 0x8000) == 0 && CanSwitchLayout?.Invoke() == true)
            {
                withheldShift = (byte)vk;
                layoutChord = true;
                Mask();
                return 1;
            }
            if (vk == VK_CAPITAL && up) Post(() => CapsPressed?.Invoke());

            if (vk == 0x20)
            {
                if (layoutSpaceHeld)
                {
                    if (up) layoutSpaceHeld = false;
                    return 1;
                }
                bool win = (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                           (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;
                if (down && win && (Native.GetAsyncKeyState(0x11) & 0x8000) == 0 &&
                    (k.flags & LLKHF_ALTDOWN) == 0 && CanSwitchLayout?.Invoke() == true)
                {
                    if (SwitchLayout(shift))
                    {
                        layoutSpaceHeld = true;
                        Mask();
                        return 1;
                    }
                }
            }
            if (vk is >= VK_VOLUME_MUTE and <= VK_MEDIA_PLAY_PAUSE)
            {
                // a key taken on its way down is also taken on its way up (and a held key repeats its down)
                if (down && (takenDown.Contains(vk) || TakeMediaKey?.Invoke(vk) == true))
                {
                    takenDown.Add(vk);
                    Post(() => MediaKey?.Invoke(vk));
                    return 1;
                }
                if (up && takenDown.Remove(vk)) return 1;
            }

            bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
            if (vk == VK_TAB && down && alt)
            {
                int dir = shift ? -1 : 1;
                if (Active) { Post(() => { Mask(); Stepped?.Invoke(dir); }); return 1; }
                if (CanTakeAltTab?.Invoke() == true)
                {
                    Active = true;
                    Post(() => { Mask(); Started?.Invoke(dir); });
                    return 1;
                }
            }
            if (Active)
            {
                if (vk == VK_TAB) return 1;   // the key-up of a swallowed Tab
                if (vk == VK_ESCAPE) { if (down) { Active = false; Post(() => Cancelled?.Invoke()); } return 1; }
                if (vk == VK_LEFT || vk == VK_RIGHT) { if (down) { int d = vk == VK_LEFT ? -1 : 1; Post(() => Stepped?.Invoke(d)); } return 1; }
                if (vk == VK_RETURN) { if (down) { Active = false; Post(() => Committed?.Invoke()); } return 1; }
                if ((vk is VK_LMENU or VK_RMENU or VK_MENU) && up) { Active = false; Post(() => Committed?.Invoke()); }
            }
        }
        catch { }
        return CallNextHookEx(hook, n, w, l);
    }

    /// <summary>Safety net, called a few times a second: Alt was let go where the hook could not see it
    /// (over a window that runs as administrator). Finish the switch instead of keeping Esc and Tab swallowed.</summary>
    public void CheckAltReleased()
    {
        if (Active && (Native.GetAsyncKeyState(VK_MENU) & 0x8000) == 0) { Active = false; Committed?.Invoke(); }
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }
}

/// <summary>The keyboard layout of the window the user types into, as two letters and a Russian name.</summary>
static class KeyboardLayout
{
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO { public int cbSize, flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public Native.RECT rcCaret; }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    static readonly Dictionary<string, string> Names = new()
    {
        ["ru"] = "Русская", ["en"] = "Английская", ["uk"] = "Украинская", ["be"] = "Белорусская", ["kk"] = "Казахская",
        ["de"] = "Немецкая", ["fr"] = "Французская", ["es"] = "Испанская", ["it"] = "Итальянская", ["pl"] = "Польская",
        ["tr"] = "Турецкая", ["pt"] = "Португальская", ["uz"] = "Узбекская", ["hy"] = "Армянская", ["ka"] = "Грузинская",
        ["az"] = "Азербайджанская", ["zh"] = "Китайская", ["ja"] = "Японская", ["ko"] = "Корейская", ["cs"] = "Чешская"
    };

    /// <summary>Language id of the layout in the foreground window, or 0.</summary>
    public static IntPtr FocusedWindow(IntPtr fg)
    {
        uint thread = Native.GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.focus != IntPtr.Zero ? info.focus : fg;
    }

    public static int Current(IntPtr fg)
    {
        uint thread = Native.GetWindowThreadProcessId(FocusedWindow(fg), out _);
        return thread == 0 ? 0 : (int)((long)Native.GetKeyboardLayout(thread) & 0xFFFF);
    }

    public static (string code, string name) Describe(int lang)
    {
        try
        {
            var ci = new CultureInfo(lang);
            var two = ci.TwoLetterISOLanguageName;
            return (two.ToUpperInvariant(), Names.TryGetValue(two, out var n) ? n : ci.Parent.DisplayName);
        }
        catch { return (null, null); }
    }
}
