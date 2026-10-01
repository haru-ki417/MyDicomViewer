using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyDicomViewer.Infrastructure;

/// <summary>
/// Windows のタイトルバーをアプリの配色（暗い色）に合わせる。
/// Windows 10 (20H1 以降) では暗いタイトルバーに、Windows 11 ではさらに枠・タイトルの色もテーマに合わせる。
/// 対応していない環境では何もしない（見た目が標準のままになるだけ）。
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // COLORREF は 0x00BBGGRR の順
    private const int CaptionColor = 0x00231C16; // #161C23（Brush.Base）
    private const int TextColor = 0x00EFE9E3;    // #E3E9EF（Brush.Text）
    private const int BorderColor = 0x00473B2F;  // #2F3B47（Brush.Line）

    /// <summary>アプリ内のすべてのウィンドウに適用されるよう登録する（起動時に1回呼ぶ）</summary>
    public static void RegisterForAllWindows()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) Apply(window);
        }));
    }

    public static void Apply(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int enabled = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            int caption = CaptionColor, text = TextColor, border = BorderColor;
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 古い Windows では無視する
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
