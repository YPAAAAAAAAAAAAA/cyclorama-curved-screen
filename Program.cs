using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WColor = System.Windows.Media.Color;
using WPoint = System.Windows.Point;

namespace Cyclorama;

// Cyclorama — a standalone, lightweight "curved screen" on the desktop that can carry any media
// (image / video / webpage) on a real concave 3D surface. Extracted and generalized from the RELIK
// native curved companion host: the companion-specific bits (shared GPU texture, head-tracking,
// avatar wiring) are gone; what's left is the pure curved-surface media carrier.
//
// Usage:
//   Cyclorama <source> [options]            source is auto-detected: image / video / http(s) URL / .html
//   Cyclorama --image photo.png
//   Cyclorama --video clip.mp4
//   Cyclorama --url https://example.com
// Options: --size WxH  --pos X,Y  --curve <0..0.8>  --flat  --still  --top  --mute
// Drag the surface to move it; drag the bottom-right grip to resize (aspect-locked); Esc closes.

public enum ContentKind { Image, Video, Web }

public sealed record ContentSpec(
    ContentKind Kind,
    string Source,
    double Width,
    double Height,
    double? Left,
    double? Top,
    double CurveDepth,
    bool Idle,
    bool AlwaysOnTop,
    bool Mute);

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var spec = ParseArgs(args);
        if (spec is null)
        {
            MessageBox.Show(
                "Cyclorama <source> [--size WxH] [--pos X,Y] [--curve 0.38] [--flat] [--still] [--top] [--mute]\n" +
                "  source: an image, a video, or http(s):// URL",
                "Cyclorama");
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new CurveWindow(spec));
    }

    private static ContentSpec? ParseArgs(string[] args)
    {
        string? source = null;
        ContentKind? forcedKind = null;
        double width = 480, height = 270;
        double? left = null, top = null;
        double curve = 0.38;
        bool idle = true, onTop = false, mute = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (a)
            {
                case "--image": forcedKind = ContentKind.Image; source = Next(); break;
                case "--video": forcedKind = ContentKind.Video; source = Next(); break;
                case "--url": case "--web": forcedKind = ContentKind.Web; source = Next(); break;
                case "--size":
                    var s = Next().Split('x', 'X', ',');
                    if (s.Length == 2 && double.TryParse(s[0], out var w) && double.TryParse(s[1], out var h)) { width = w; height = h; }
                    break;
                case "--pos":
                    var p = Next().Split(',', 'x');
                    if (p.Length == 2 && double.TryParse(p[0], out var px) && double.TryParse(p[1], out var py)) { left = px; top = py; }
                    break;
                case "--curve": if (double.TryParse(Next(), out var c)) curve = Math.Clamp(c, 0, 0.8); break;
                case "--flat": curve = 0; break;
                case "--still": idle = false; break;
                case "--top": onTop = true; break;
                case "--mute": mute = true; break;
                default: if (!a.StartsWith('-')) source = a; break;
            }
        }

        source ??= FindDefaultSample();          // no source given => open with a bundled space image
        if (string.IsNullOrWhiteSpace(source)) return null;
        var kind = forcedKind ?? DetectKind(source);
        return new ContentSpec(kind, source, width, height, left, top, curve, idle, onTop, mute);
    }

    // With no argument, show a bundled sample so double-clicking the exe just works. Looks for
    // samples/cosmic-cliffs.jpg next to the exe (published layout) or up a few parents (repo layout).
    private static string? FindDefaultSample()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            var p = Path.Combine(dir, "samples", "cosmic-cliffs.jpg");
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        }
        return null;
    }

    private static ContentKind DetectKind(string source)
    {
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return ContentKind.Web;
        var ext = Path.GetExtension(source).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".webm" or ".mov" or ".mkv" or ".avi" or ".m4v" or ".wmv" => ContentKind.Video,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tiff" => ContentKind.Image,
            ".html" or ".htm" => ContentKind.Web,
            _ => ContentKind.Image,
        };
    }
}

public sealed class CurveWindow : Window
{
    private const double PanelWidth = 3.2;   // curve span (X); aspect comes from the source, not the mesh
    private const double PaddingRatio = 0.10;
    private const double RestYaw = -5;        // default lean when the cursor is away
    private const double MaxTrackAngle = 10;  // max tilt toward the cursor
    private const double FollowSpeed = 0.22;  // easing speed toward the cursor target

    private readonly ContentSpec spec;
    private readonly AxisAngleRotation3D idleYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D idlePitch = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D idleRoll = new(new Vector3D(0, 0, 1), 0);
    private readonly TranslateTransform3D idleFloat = new(0, 0, 0);
    private readonly AxisAngleRotation3D mouseYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D mousePitch = new(new Vector3D(1, 0, 0), 0);
    private double currentYaw = RestYaw, currentPitch, targetYaw = RestYaw, targetPitch;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private MediaPlayer? videoPlayer;
    private ImageBrush? webBrush;
    private WebSurface? webSurface;
    private double aspect = 16.0 / 9.0;
    private bool adjustingSize;

    // video-player controls (only built when the source is a video)
    private Slider? seekBar;
    private TextBlock? timeLabel;
    private Button? playButton;
    private Border? controlBar;
    private DispatcherTimer? hideControlsTimer;
    private bool scrubbing, updatingSeek, isPaused;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const int WM_SIZING = 0x0214;
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Constrain interactive resize to the content aspect at the OS level — smoother than correcting
    // Height after each SizeChanged. WMSZ edge codes: 1 L, 2 R, 3 T, 4 TL, 5 TR, 6 B, 7 BL, 8 BR.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_SIZING || aspect <= 0) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<RECT>(lParam);
        var edge = wParam.ToInt32();
        double w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (edge is 3 or 6) w = h * aspect; else h = w / aspect;   // pure top/bottom drag => width follows
        if (w < 200) { w = 200; h = w / aspect; }
        if (edge is 3 or 4 or 5) r.Top = r.Bottom - (int)Math.Round(h); else r.Bottom = r.Top + (int)Math.Round(h);
        if (edge is 1 or 4 or 7) r.Left = r.Right - (int)Math.Round(w); else r.Right = r.Left + (int)Math.Round(w);
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    public CurveWindow(ContentSpec spec)
    {
        this.spec = spec;
        Title = "Cyclorama";
        Width = spec.Width;
        Height = spec.Height;
        if (spec.Left is double l) Left = l;
        if (spec.Top is double t) Top = t;
        MinWidth = 160; MinHeight = 90;
        WindowStartupLocation = spec.Left is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AllowsTransparency = true;
        Background = new SolidColorBrush(WColor.FromArgb(1, 0, 0, 0)); // ~transparent but hit-testable
        Topmost = spec.AlwaysOnTop;
        ShowInTaskbar = true;

        var material = BuildContentMaterial();
        Content = BuildScene(material);
        LockHeightToAspect();              // start shaped like the content
        SizeChanged += (_, _) => LockHeightToAspect();   // programmatic aspect changes (e.g. video opens)
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is ResizeGrip) return;          // let the grip resize, don't move the window
            if (controlBar?.IsMouseOver == true) return;         // using the player controls, don't drag
            if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
        };
        CompositionTarget.Rendering += OnRendering;
        if (spec.Kind == ContentKind.Web && webBrush != null)
            Loaded += (_, _) => webSurface ??= new WebSurface(spec.Source, 1280, 720, webBrush, 30);
        if (spec.Kind == ContentKind.Video)
        {
            hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hideControlsTimer.Tick += (_, _) => { hideControlsTimer!.Stop(); if (controlBar != null) controlBar.Opacity = 0; };
            PreviewMouseMove += (_, _) => ShowControls();
            Loaded += (_, _) => ShowControls();
        }
        Closed += (_, _) => { CompositionTarget.Rendering -= OnRendering; videoPlayer?.Close(); webSurface?.Dispose(); };
    }

    // ---- scene -------------------------------------------------------------

    private UIElement BuildScene(Material material)
    {
        var root = new Grid { Background = new SolidColorBrush(WColor.FromArgb(1, 0, 0, 0)) };
        root.Children.Add(BuildViewport(material));               // [0] — stays index 0 for RebuildViewport
        if (spec.Kind == ContentKind.Video) root.Children.Add(BuildControls());
        root.Children.Add(new ResizeGrip                           // added last => sits on top
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 8, 8),
            Opacity = 0.5,
        });
        return root;
    }

    private Viewport3D BuildViewport(Material material)
    {
        var viewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 0, 4.2), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 46),
        };
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(WColor.FromRgb(255, 255, 255)));
        group.Children.Add(BuildCurvedPanel(material));
        viewport.Children.Add(new ModelVisual3D { Content = group });
        return viewport;
    }

    // The concave parabolic panel: a 64x20 grid where z = +curveDepth * nx^2 lifts the edges toward the
    // camera (concave — the screen wraps in at the sides). Height is derived from the content aspect so
    // the media is never stretched.
    private GeometryModel3D BuildCurvedPanel(Material material)
    {
        const int columns = 64, rows = 20;
        double width = PanelWidth;
        double height = width / aspect;
        double curveDepth = spec.CurveDepth;

        var mesh = new MeshGeometry3D();
        for (var y = 0; y <= rows; y++)
        {
            var v = y / (double)rows;
            for (var x = 0; x <= columns; x++)
            {
                var u = x / (double)columns;
                var nx = (u - 0.5) * 2;
                // +curveDepth*nx^2: edges sit nearer the camera than the centre => CONCAVE (the screen
                // wraps toward the viewer at the edges). A negative sign here would bulge it convex.
                mesh.Positions.Add(new Point3D(nx * width / 2, (0.5 - v) * height, curveDepth * nx * nx));
                mesh.TextureCoordinates.Add(new WPoint(u, v));
            }
        }
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            var i = y * (columns + 1) + x;
            mesh.TriangleIndices.Add(i);
            mesh.TriangleIndices.Add(i + columns + 1);
            mesh.TriangleIndices.Add(i + 1);
            mesh.TriangleIndices.Add(i + 1);
            mesh.TriangleIndices.Add(i + columns + 1);
            mesh.TriangleIndices.Add(i + columns + 2);
        }

        var transform = new Transform3DGroup();
        transform.Children.Add(idleFloat);
        transform.Children.Add(new ScaleTransform3D(1 - PaddingRatio, 1 - PaddingRatio, 1));
        transform.Children.Add(new RotateTransform3D(idleYaw));
        transform.Children.Add(new RotateTransform3D(idlePitch));
        transform.Children.Add(new RotateTransform3D(idleRoll));
        transform.Children.Add(new RotateTransform3D(mouseYaw));
        transform.Children.Add(new RotateTransform3D(mousePitch));
        return new GeometryModel3D(mesh, material) { BackMaterial = material, Transform = transform };
    }

    // ---- content sources ---------------------------------------------------

    private Material BuildContentMaterial() => spec.Kind switch
    {
        ContentKind.Image => BuildImageMaterial(spec.Source),
        ContentKind.Video => BuildVideoMaterial(spec.Source),
        ContentKind.Web => BuildWebMaterial(),
        _ => BuildPlaceholderMaterial("?"),
    };

    // The web page renders offscreen at 1280x720 (16:9) and is copied frame-by-frame onto this brush.
    private Material BuildWebMaterial()
    {
        webBrush = new ImageBrush { Stretch = Stretch.Fill };
        aspect = 16.0 / 9.0;
        return new DiffuseMaterial(webBrush);
    }

    private Material BuildImageMaterial(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0) aspect = (double)bmp.PixelWidth / bmp.PixelHeight;
            var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
            return new DiffuseMaterial(brush);
        }
        catch (Exception e) { return BuildPlaceholderMaterial("image error: " + e.Message); }
    }

    private Material BuildVideoMaterial(string path)
    {
        try
        {
            videoPlayer = new MediaPlayer { Volume = spec.Mute ? 0 : 0.6 };
            videoPlayer.MediaOpened += (_, _) =>
            {
                if (videoPlayer.NaturalVideoWidth > 0 && videoPlayer.NaturalVideoHeight > 0)
                {
                    aspect = (double)videoPlayer.NaturalVideoWidth / videoPlayer.NaturalVideoHeight;
                    Dispatcher.Invoke(RebuildPanel);
                }
            };
            videoPlayer.MediaEnded += (_, _) => { videoPlayer!.Position = TimeSpan.Zero; videoPlayer.Play(); };
            videoPlayer.MediaFailed += (_, ev) =>
                Dispatcher.Invoke(() => RebuildViewport(BuildPlaceholderMaterial("video failed:\n" + ev.ErrorException?.Message)));
            videoPlayer.Open(new Uri(Path.GetFullPath(path)));
            videoPlayer.Play();
            var drawing = new VideoDrawing { Player = videoPlayer, Rect = new Rect(0, 0, 1, 1) };
            var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
            return new DiffuseMaterial(brush);
        }
        catch (Exception e) { return BuildPlaceholderMaterial("video error: " + e.Message); }
    }

    private static Material BuildPlaceholderMaterial(string label)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new LinearGradientBrush(WColor.FromRgb(10, 22, 40), WColor.FromRgb(30, 120, 170), 90),
            null, new RectangleGeometry(new Rect(0, 0, 1280, 720))));
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 48, Brushes.White, 1.0);
        group.Children.Add(new GeometryDrawing(Brushes.White, null,
            text.BuildGeometry(new WPoint(80, 320))));
        return new DiffuseMaterial(new DrawingBrush(group) { Stretch = Stretch.Fill });
    }

    // Keep the window the same shape as the content so the curve is never stretched. The grip then
    // resizes by width; height follows. Re-entrancy guarded (setting Height re-fires SizeChanged).
    private void LockHeightToAspect()
    {
        if (adjustingSize || aspect <= 0 || Width <= 0) return;
        var target = Width / aspect;
        if (Math.Abs(target - Height) > 0.5)
        {
            adjustingSize = true;
            Height = target;
            adjustingSize = false;
        }
    }

    private void RebuildViewport(Material material)
    {
        if (Content is Grid root && root.Children.Count > 0 && root.Children[0] is Viewport3D)
        {
            root.Children.RemoveAt(0);
            root.Children.Insert(0, BuildViewport(material));
        }
    }

    private void RebuildPanel()
    {
        RebuildViewport(BuildReuseMaterial());
        LockHeightToAspect();
    }

    // reuse the already-created brush (video/image) when only the aspect changed
    private Material BuildReuseMaterial()
    {
        if (videoPlayer != null)
            return new DiffuseMaterial(new DrawingBrush(new VideoDrawing { Player = videoPlayer, Rect = new Rect(0, 0, 1, 1) }) { Stretch = Stretch.Fill });
        return BuildContentMaterial();
    }

    // ---- video player controls --------------------------------------------

    // A flat 2D control bar overlaid on the bottom of the window (the video itself stays on the curve).
    // Auto-hides when the mouse is still; shows on movement.
    private UIElement BuildControls()
    {
        Button Btn(string glyph) => new()
        {
            Content = glyph, Width = 32, Height = 28, FontSize = 14, Foreground = Brushes.White,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };

        playButton = Btn("⏸");                                  // ⏸
        playButton.Click += (_, _) => TogglePlay();

        seekBar = new Slider
        {
            Minimum = 0, Maximum = 1, Value = 0, IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
        };
        seekBar.ValueChanged += (_, e) => { if (!updatingSeek && videoPlayer != null) videoPlayer.Position = TimeSpan.FromSeconds(e.NewValue); };
        seekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => scrubbing = true));
        seekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => scrubbing = false));

        timeLabel = new TextBlock
        {
            Text = "0:00 / 0:00", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(2, 0, 6, 0),
        };

        var muteBtn = Btn(spec.Mute ? "\U0001F507" : "\U0001F50A");  // 🔇 / 🔊
        var volBar = new Slider
        {
            Minimum = 0, Maximum = 1, Value = videoPlayer?.Volume ?? 0.6, Width = 60,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volBar.ValueChanged += (_, e) =>
        {
            if (videoPlayer != null) videoPlayer.Volume = e.NewValue;
            muteBtn.Content = e.NewValue <= 0.001 ? "\U0001F507" : "\U0001F50A";
        };
        muteBtn.Click += (_, _) =>
        {
            if (volBar.Value > 0.001) { volBar.Tag = volBar.Value; volBar.Value = 0; }
            else { volBar.Value = volBar.Tag is double d ? d : 0.6; }
        };

        var grid = new Grid { Margin = new Thickness(8, 3, 8, 3) };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Place(UIElement el, int col) { Grid.SetColumn(el, col); grid.Children.Add(el); }
        Place(playButton, 0); Place(seekBar, 1); Place(timeLabel, 2); Place(muteBtn, 3); Place(volBar, 4);

        controlBar = new Border
        {
            Background = new SolidColorBrush(WColor.FromArgb(185, 10, 14, 20)),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 12),
            Child = grid,
        };
        return controlBar;
    }

    private void TogglePlay()
    {
        if (videoPlayer == null) return;
        if (isPaused) { videoPlayer.Play(); isPaused = false; }
        else { videoPlayer.Pause(); isPaused = true; }
        if (playButton != null) playButton.Content = isPaused ? "▶" : "⏸";   // ▶ / ⏸
    }

    private void ShowControls()
    {
        if (controlBar == null) return;
        controlBar.Opacity = 1;
        hideControlsTimer?.Stop();
        hideControlsTimer?.Start();
    }

    private void UpdatePlaybackUi()
    {
        if (videoPlayer == null || seekBar == null || !videoPlayer.NaturalDuration.HasTimeSpan) return;
        var dur = videoPlayer.NaturalDuration.TimeSpan;
        if (dur.TotalSeconds > 0 && Math.Abs(seekBar.Maximum - dur.TotalSeconds) > 0.05) seekBar.Maximum = dur.TotalSeconds;
        if (!scrubbing)
        {
            updatingSeek = true;
            seekBar.Value = videoPlayer.Position.TotalSeconds;
            updatingSeek = false;
        }
        if (timeLabel != null) timeLabel.Text = $"{Fmt(videoPlayer.Position)} / {Fmt(dur)}";
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---- idle drift --------------------------------------------------------

    // Per-frame: lean the surface toward the cursor (the parallax tilt from the original host), then —
    // unless --still — add a gentle idle drift on top.
    private void OnRendering(object? sender, EventArgs e)
    {
        UpdateTiltFromCursor();
        currentYaw += (targetYaw - currentYaw) * FollowSpeed;
        currentPitch += (targetPitch - currentPitch) * FollowSpeed;
        mouseYaw.Angle = currentYaw;
        mousePitch.Angle = currentPitch;
        UpdatePlaybackUi();

        if (!spec.Idle) return;
        var t = (DateTime.UtcNow - startedAt).TotalSeconds;
        idleYaw.Angle = Math.Sin(t * 0.58) * 0.8;
        idlePitch.Angle = Math.Cos(t * 0.44) * 0.45;
        idleRoll.Angle = Math.Sin(t * 0.36) * 0.35;
        idleFloat.OffsetY = Math.Sin(t * 0.95) * 0.075;
        idleFloat.OffsetX = Math.Cos(t * 0.52) * 0.075 * 0.22;
        idleFloat.OffsetZ = Math.Sin(t * 0.41) * 0.075 * 0.45;
    }

    // Poll the global cursor each frame (more reliable than MouseMove over 3D content). DPI-safe:
    // both the cursor and the window bounds are in physical pixels. Outside the window => rest lean.
    private void UpdateTiltFromCursor()
    {
        if (PresentationSource.FromVisual(this) is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var tl = PointToScreen(new WPoint(0, 0));
        var br = PointToScreen(new WPoint(ActualWidth, ActualHeight));
        if (!GetCursorPos(out var c)) return;
        if (c.X < tl.X || c.X > br.X || c.Y < tl.Y || c.Y > br.Y) { targetYaw = RestYaw; targetPitch = 0; return; }
        var nx = ((c.X - tl.X) / (br.X - tl.X) - 0.5) * 2;
        var ny = ((c.Y - tl.Y) / (br.Y - tl.Y) - 0.5) * 2;
        targetYaw = RestYaw + MaxTrackAngle * Math.Clamp(nx, -1, 1);
        targetPitch = -MaxTrackAngle * Math.Clamp(ny, -1, 1);
    }
}
