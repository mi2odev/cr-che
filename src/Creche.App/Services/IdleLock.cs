using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Creche.Application.Common;
using Creche.Application.Settings;

namespace Creche.App.Services;

/// <summary>
/// Locks the application after a period without keyboard or mouse activity, so that the next person at a shared PC
/// does not find the previous user still signed in. Activity is measured for the whole Windows session
/// (GetLastInputInfo), so someone working in another program next to the app does not get locked out.
/// </summary>
public sealed class IdleLock
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly ICurrentUser _user;
    private readonly DispatcherTimer _timer;

    public IdleLock(ICurrentUser user)
    {
        _user = user;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>Minutes of inactivity before locking; 0 disables auto-lock. Updated whenever the settings are read.</summary>
    public int Minutes { get; set; } = CrecheSettings.DefaultAutoLockMinutes;

    /// <summary>Raised on the UI thread when the idle limit is reached while someone is signed in.</summary>
    public event EventHandler? IdleLimitReached;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Check()
    {
        if (Minutes <= 0 || !_user.IsSignedIn) return;
        if (IdleTime() < TimeSpan.FromMinutes(Minutes)) return;
        // A Windows dialog (open/save file, print) runs its own modal loop and cannot be closed like a WPF window:
        // cancel it now and lock on the next check, once it is gone.
        if (CancelSystemDialogs()) return;
        IdleLimitReached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sends "close" (= Cancel) to the standard Windows dialogs open on the UI thread. True if there were any.</summary>
    private static bool CancelSystemDialogs()
    {
        var dialogs = new List<IntPtr>();
        var className = new StringBuilder(16);
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            className.Clear();
            if (IsWindowVisible(hwnd) && GetClassName(hwnd, className, className.Capacity) > 0 && className.ToString() == DialogClass)
                dialogs.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var hwnd in dialogs) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        return dialogs.Count > 0;
    }

    /// <summary>Time since the last keyboard or mouse input anywhere in this Windows session.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Both values are milliseconds since boot and wrap after ~49.7 days: unsigned subtraction handles the wrap.
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>Window class of standard Windows dialog boxes (common file dialogs, print dialog, message boxes).</summary>
    private const string DialogClass = "#32770";
    private const uint WmClose = 0x0010;

    private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc callback, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);
}
