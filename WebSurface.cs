using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CurveScreen;

// Renders a live web page onto the curved surface. WebView2 is an HWND/airspace control, so it can't
// be a 3D material brush directly; instead we host it in a far-offscreen window, let it render at a
// fixed resolution, and copy frames onto an ImageBrush on a timer. The Edge WebView2 runtime is shared
// (not bundled) so this stays lightweight.
public sealed class WebSurface : IDisposable
{
    private readonly Window host;
    private readonly WebView2 web;
    private readonly DispatcherTimer timer;
    private readonly ImageBrush target;
    private bool ready;
    private bool capturing;
    private bool disposed;

    public WebSurface(string url, int renderWidth, int renderHeight, ImageBrush target, int fps)
    {
        this.target = target;
        web = new WebView2();
        // Park the host fully to the left of every monitor so it never shows, but stays a real, non-
        // minimized window (WebView2 keeps rendering offscreen; minimized windows would suspend).
        host = new Window
        {
            Width = renderWidth,
            Height = renderHeight,
            Left = SystemParameters.VirtualScreenLeft - renderWidth - 64,
            Top = SystemParameters.VirtualScreenTop,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Content = web,
        };
        host.Show();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(16, 1000.0 / Math.Clamp(fps, 1, 60))) };
        timer.Tick += (_, _) => _ = CaptureAsync();
        _ = InitAsync(url);
    }

    private async Task InitAsync(string url)
    {
        try
        {
            // Keep the browser cache/profile out of the app folder.
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "CurveScreen.WebView2"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.DefaultBackgroundColor = System.Drawing.Color.Black;
            // Accept a bare local path (e.g. page.html) as well as http(s):// and file:// URLs.
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                url = new Uri(Path.GetFullPath(url)).AbsoluteUri;
            web.CoreWebView2.NavigationCompleted += (_, ev) =>
            {
                if (!ev.IsSuccess) target.ImageSource = RenderNotice("couldn't load page\n" + ev.WebErrorStatus);
            };
            web.CoreWebView2.Navigate(url);
            ready = true;
            timer.Start();
        }
        catch (Exception e)
        {
            target.ImageSource = RenderNotice("web unavailable\n" + e.Message);
        }
    }

    private async Task CaptureAsync()
    {
        if (!ready || capturing || disposed || web.CoreWebView2 is null) return;
        capturing = true;
        try
        {
            using var ms = new MemoryStream();
            // JPEG encodes far faster than PNG per frame -> noticeably higher live frame rate. Web pages
            // are opaque (DefaultBackgroundColor is black) so we don't need PNG's alpha channel.
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            if (disposed) return;
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            target.ImageSource = bmp;
        }
        catch { /* drop this frame, keep going */ }
        finally { capturing = false; }
    }

    private static BitmapSource RenderNotice(string text)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(12, 16, 24)), null, new Rect(0, 0, 1280, 720));
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 34, Brushes.White, 1.0) { MaxTextWidth = 1120 };
            dc.DrawText(ft, new Point(80, 300));
        }
        var rtb = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        try { web.Dispose(); } catch { }
        try { host.Close(); } catch { }
    }
}
