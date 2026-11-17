using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Creche.App.Services;

/// <summary>Swaps the colour dictionary (Resources/Themes/Dark.xaml or Light.xaml) at runtime.</summary>
public sealed class ThemeService(UiState state)
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public string Current { get; private set; } = Dark;
    public bool IsDark => Current == Dark;
    public event EventHandler? Changed;

    public void ApplySaved() => Apply(state.Get("theme", Dark));

    public void Toggle() => Apply(IsDark ? Light : Dark);

    public void Apply(string theme)
    {
        Current = theme == Light ? Light : Dark;
        var dictionary = new ResourceDictionary { Source = new Uri($"Resources/Themes/{Current}.xaml", UriKind.Relative) };
        System.Windows.Application.Current.Resources.MergedDictionaries[0] = dictionary;
        state.Set("theme", Current);
        foreach (Window window in System.Windows.Application.Current.Windows) ApplyTitleBar(window);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks Windows to draw the title bar dark or light to match the theme (Windows 10 2004+).</summary>
    public void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
