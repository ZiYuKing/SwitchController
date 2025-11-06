using LibVLCSharp.Shared;
using Microsoft.Win32;
using SwitchController.Properties;
using SysBot.Base;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static SysBot.Base.SwitchButton;

namespace SwitchController;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static SwitchConnectionConfig Config = new() { Protocol = SwitchProtocol.WiFi, IP = "192.168.0.0", Port = 6000 };
    private static SwitchSocketAsync SwitchConnection = new(Config);

    private static SwitchSocketAsync? CON { get; set; }
    private static CancellationTokenSource? SOUR { get; set; }
    private CancellationTokenSource Source = new();

    private LibVLC? _libVLC;
    private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;

    private bool isHolding = false;
    private CancellationTokenSource holdToken;

    private const short StickMax = 32767;
    private const double DeadZonePx = 8;     // 小于该半径当作 (0,0)
    private const int SendIntervalMs = 15;   // 发送节流（毫秒）
    private const double TapThresholdPx = 6; // 轻点最大位移（像素）
    private const int TapThresholdMs = 200;  // 轻点最大时长（毫秒）

    public Point LeftStickValue { get; private set; } = new Point(0, 0);
    public Point RightStickValue { get; private set; } = new Point(0, 0);

    // 左摇杆
    private TranslateTransform _knobTTLift;
    private double _maxRadiusLift;
    private readonly Stopwatch _leftSw = new();
    private long _leftLastSendMs = 0;
    private double _leftMaxDrag = 0;

    // 右摇杆
    private TranslateTransform _knobTTRight;
    private double _maxRadiusRight;
    private readonly Stopwatch _rightSw = new();
    private long _rightLastSendMs = 0;
    private double _rightMaxDrag = 0;

    private byte[] _screenGrab = [];
    private bool _rtspStreaming = false;
    public MainWindow()
    {
        InitializeComponent();
        btnRtspRescue.Visibility = Settings.Default.StreamMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        this.Loaded += (_, __) => LoadSettings();
        this.Closing += (_, __) => SaveSettings();

        // 初始化 LibVLC
        Core.Initialize();
        _libVLC = new LibVLC();
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
        VlcView.MediaPlayer = _mediaPlayer;

    }

    private void LoadSettings()
    {
        var ip = Settings.Default.SwitchIP;
        var port = Settings.Default.SwitchPort;

        if (!string.IsNullOrWhiteSpace(ip))
            txtIpAddress.Text = ip;

        if (port > 0)
            txtPort.Text = port.ToString();
    }

    private void SaveSettings()
    {

        if (int.TryParse(txtPort.Text, out var p) && p > 0 && p <= 65535)
            Settings.Default.SwitchPort = p;

        string text = txtIpAddress.Text;
        string pattern = @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$";
        bool isValid = Regex.IsMatch(text, pattern);
        if (!isValid)
        {
            return;
        }
        Settings.Default.SwitchIP = txtIpAddress.Text.Trim();
        Settings.Default.Save(); // 写入本地
    }

    private static short ClampStick(double norm)
        => (short)Math.Round(Math.Max(-1, Math.Min(1, norm)) * StickMax);

    private void ThrottledSendStick(SwitchStick stick, short x, short y, ref long lastSendMs, Stopwatch sw)
    {
        long now = sw.ElapsedMilliseconds;
        if (now - lastSendMs >= SendIntervalMs)
        {
            lastSendMs = now;
            _ = SetStick(stick, x, y);
        }
    }

    private void LeftStickThumb_Loaded(object sender, RoutedEventArgs e)
    {
        _knobTTLift = LeftStickKnobTT;

        var root = LeftStickRoot;
        var thumb = LeftStickThumb;

        double baseDiameter = Math.Min(root.ActualWidth, root.ActualHeight);
        _maxRadiusLift = (baseDiameter - thumb.ActualWidth) / 2.0;

        root.SizeChanged += (_, __) =>
        {
            baseDiameter = Math.Min(root.ActualWidth, root.ActualHeight);
            _maxRadiusLift = (baseDiameter - thumb.ActualWidth) / 2.0;
            ClampKnob();
            UpdateNormalizedLift();
        };

        _leftSw.Restart();
        _leftLastSendMs = 0;
        _leftMaxDrag = 0;
    }

    private void LeftStickThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _leftSw.Restart();
        _leftMaxDrag = 0;
    }

    private void LeftStickThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_knobTTLift == null) return;

        double nx = _knobTTLift.X + e.HorizontalChange;
        double ny = _knobTTLift.Y + e.VerticalChange;

        double len = Math.Sqrt(nx * nx + ny * ny);
        if (len > _maxRadiusLift && len > 0)
        {
            double scale = _maxRadiusLift / len;
            nx *= scale;
            ny *= scale;
        }

        _knobTTLift.X = nx;
        _knobTTLift.Y = ny;

        UpdateNormalizedLift();

        _leftMaxDrag = Math.Max(_leftMaxDrag, Math.Sqrt(nx * nx + ny * ny));
    }

    private void LeftStickThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_knobTTLift == null) return;

        var animX = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var animY = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        animX.Completed += async (_, __) =>
        {
            _knobTTLift.BeginAnimation(TranslateTransform.XProperty, null);
            _knobTTLift.BeginAnimation(TranslateTransform.YProperty, null);
            _knobTTLift.X = 0;
            _knobTTLift.Y = 0;

            // 复原到 (0,0)
            await SetStick(SwitchStick.LEFT, 0, 0).ConfigureAwait(false);
        };

        _knobTTLift.BeginAnimation(TranslateTransform.XProperty, animX);
        _knobTTLift.BeginAnimation(TranslateTransform.YProperty, animY);

        bool isTap = _leftMaxDrag < TapThresholdPx && _leftSw.ElapsedMilliseconds < TapThresholdMs;
        if (isTap)
            _ = Click(LSTICK);

        LeftStickValue = new Point(0, 0);
        _leftSw.Restart();
        _leftMaxDrag = 0;
    }

    private void UpdateNormalizedLift()
    {
        double dx = _knobTTLift.X;
        double dy = _knobTTLift.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double nx = (_maxRadiusLift <= 0.0001) ? 0 : dx / _maxRadiusLift;
        double ny = (_maxRadiusLift <= 0.0001) ? 0 : -dy / _maxRadiusLift;

        if (dist < DeadZonePx) { nx = 0; ny = 0; }

        LeftStickValue = new Point(nx, ny);

        short sx = ClampStick(nx);
        short sy = ClampStick(ny);

        ThrottledSendStick(SwitchStick.LEFT, sx, sy, ref _leftLastSendMs, _leftSw);
    }

    private void ClampKnob()
    {
        double nx = _knobTTLift.X;
        double ny = _knobTTLift.Y;
        double len = Math.Sqrt(nx * nx + ny * ny);
        if (len > _maxRadiusLift && len > 0)
        {
            double scale = _maxRadiusLift / len;
            _knobTTLift.X = nx * scale;
            _knobTTLift.Y = ny * scale;
        }
    }


    private void RightStickThumb_Loaded(object sender, RoutedEventArgs e)
    {
        _knobTTRight = RightStickKnobTT;

        var root = RightStickRoot;
        var thumb = RightStickThumb;

        double baseDiameter = Math.Min(root.ActualWidth, root.ActualHeight);
        _maxRadiusRight = (baseDiameter - thumb.ActualWidth) / 2.0;

        root.SizeChanged += (_, __) =>
        {
            baseDiameter = Math.Min(root.ActualWidth, root.ActualHeight);
            _maxRadiusRight = (baseDiameter - thumb.ActualWidth) / 2.0;
            ClampKnobRight();
            UpdateNormalizedRight();
        };

        _rightSw.Restart();
        _rightLastSendMs = 0;
        _rightMaxDrag = 0;
    }

    private void RightStickThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _rightSw.Restart();
        _rightMaxDrag = 0;
    }

    private void RightStickThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_knobTTRight == null) return;

        double nx = _knobTTRight.X + e.HorizontalChange;
        double ny = _knobTTRight.Y + e.VerticalChange;

        double len = Math.Sqrt(nx * nx + ny * ny);
        if (len > _maxRadiusRight && len > 0)
        {
            double scale = _maxRadiusRight / len;
            nx *= scale;
            ny *= scale;
        }

        _knobTTRight.X = nx;
        _knobTTRight.Y = ny;

        UpdateNormalizedRight();

        _rightMaxDrag = Math.Max(_rightMaxDrag, Math.Sqrt(nx * nx + ny * ny));
    }

    private void RightStickThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_knobTTRight == null) return;

        var animX = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var animY = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        animX.Completed += async (_, __) =>
        {
            _knobTTRight.BeginAnimation(TranslateTransform.XProperty, null);
            _knobTTRight.BeginAnimation(TranslateTransform.YProperty, null);
            _knobTTRight.X = 0;
            _knobTTRight.Y = 0;

            await SetStick(SwitchStick.RIGHT, 0, 0).ConfigureAwait(false);
        };

        _knobTTRight.BeginAnimation(TranslateTransform.XProperty, animX);
        _knobTTRight.BeginAnimation(TranslateTransform.YProperty, animY);

        bool isTap = _rightMaxDrag < TapThresholdPx && _rightSw.ElapsedMilliseconds < TapThresholdMs;
        if (isTap)
            _ = Click(RSTICK);

        RightStickValue = new Point(0, 0);
        _rightSw.Restart();
        _rightMaxDrag = 0;
    }

    private void UpdateNormalizedRight()
    {
        double dx = _knobTTRight.X;
        double dy = _knobTTRight.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double nx = (_maxRadiusRight <= 0.0001) ? 0 : dx / _maxRadiusRight;
        double ny = (_maxRadiusRight <= 0.0001) ? 0 : -dy / _maxRadiusRight;

        if (dist < DeadZonePx) { nx = 0; ny = 0; }

        RightStickValue = new Point(nx, ny);

        short sx = ClampStick(nx);
        short sy = ClampStick(ny);

        ThrottledSendStick(SwitchStick.RIGHT, sx, sy, ref _rightLastSendMs, _rightSw);
    }

    private void ClampKnobRight()
    {
        double nx = _knobTTRight.X;
        double ny = _knobTTRight.Y;
        double len = Math.Sqrt(nx * nx + ny * ny);
        if (len > _maxRadiusRight && len > 0)
        {
            double scale = _maxRadiusRight / len;
            _knobTTRight.X = nx * scale;
            _knobTTRight.Y = ny * scale;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOverInteractive(e.OriginalSource as DependencyObject))
            return;

        try { DragMove(); } catch {  }
    }

    private static bool IsOverInteractive(DependencyObject? src)
    {
        return FindAncestor<Button>(src) != null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void btnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void btnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void btnStartStream_Click(object sender, RoutedEventArgs e)
    {
        SwitchConnection = new SwitchSocketAsync(new SwitchConnectionConfig() { Protocol = SwitchProtocol.WiFi, IP = txtIpAddress.Text, Port = int.Parse(txtPort.Text) });
        CON = SwitchConnection;
        SOUR = Source;
        btnStopStream.IsEnabled = true;
        btnCapture.IsEnabled = true;
        btnStartStream.IsEnabled = false;
        await Connect();
        SaveSettings();
    }

    private void txtIpAddress_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = txtIpAddress.Text;
        string pattern = @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$";
        bool isValid = Regex.IsMatch(text, pattern);
        if (!isValid)
        {
            return;
        }
        //Settings.Default.SwitchIP = text;
        //Settings.Default.Save();
    }

    private void txtPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(txtPort.Text, out int port) && port > 0 && port < 65536)
        {
            //Settings.Default.SwitchPort = port;
            //Settings.Default.Save();
        }
    }

    private async Task Connect()
    {
        txtIpAddress.IsEnabled = false;
        txtPort.IsEnabled = false;

        try
        {
            if (SwitchConnection.Connected) return;

            // 5 秒超时
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = cts.Token;

            var connectTask = Task.Run(() => SwitchConnection.Connect(), token);
            var winner = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, token));

            if (winner != connectTask)
            {
                // 理论上不会进来
                throw new TimeoutException("连接等待被取消。");
            }

            // 将 Connect 的异常抛出
            await connectTask;

            if (!SwitchConnection.Connected)
                throw new SocketException((int)SocketError.NotConnected);

            // 更新 UI
            Dispatcher.Invoke(() =>
            {
                txtLog.Text = "Switch | 正在连接...";
                ellipseStatus.Fill = Brushes.Green;
                txtStatus.Text = "已连接";
            });

            await Task.Delay(300, token).ConfigureAwait(false);

            string id = await SwitchConnection.GetTitleID(token).ConfigureAwait(false);

            Dispatcher.Invoke(() =>
            {
                txtLog.Text = id != "0000000000000000"
                    ? $"Switch | 已连接成功 | {id}"
                    : "Switch | 已连接成功 | 未启动游戏";
            });
            // 操作预览
            StartScreenStream();
            // 持续预览
            TriggerPreview(TimeSpan.FromSeconds(2));
            // Rtsp播放
            await Dispatcher.InvokeAsync(() =>
            {
                StartRtspStream($"rtsp://{txtIpAddress.Text}:{Settings.Default.RtspPort}/");
            });
        }
        catch (OperationCanceledException)
        {
            if (SwitchConnection.Connected)
                SwitchConnection.Disconnect();

            Dispatcher.Invoke(() =>
            {
                ellipseStatus.Fill = Brushes.Red;
                txtStatus.Text = "未连接";
                MessageBox.Show("连接失败：超过 5 秒未建立连接（超时）");
                txtLog.Text = "Switch | 连接超时";
            });
        }
        catch (SocketException ex)
        {
            try
            {
                if (SwitchConnection.Connected)
                    await SwitchConnection.SendAsync(SwitchCommand.DetachController(true), CancellationToken.None).ConfigureAwait(false);
            }
            catch {  }
            finally
            {
                if (SwitchConnection.Connected)
                    SwitchConnection.Disconnect();
            }

            if (ex.Message.Contains("未能响应") || ex.Message.Contains("主动拒绝"))
            {
                Dispatcher.Invoke(() => MessageBox.Show($"连接失败：{ex.Message}"));
            }
            else
            {
                Dispatcher.Invoke(() => MessageBox.Show($"连接失败：{ex.SocketErrorCode} - {ex.Message}"));
            }

            Dispatcher.Invoke(() =>
            {
                ellipseStatus.Fill = Brushes.Red;
                txtStatus.Text = "未连接";
                txtLog.Text = "Switch | 连接失败";
            });
        }
        catch (Exception ex)
        {
            if (SwitchConnection.Connected)
                SwitchConnection.Disconnect();

            Dispatcher.Invoke(() =>
            {
                ellipseStatus.Fill = Brushes.Red;
                txtStatus.Text = "未连接";
                MessageBox.Show($"连接失败：{ex.Message}");
                txtLog.Text = "Switch | 连接异常";
            });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                txtIpAddress.IsEnabled = true;
                txtPort.IsEnabled = true;
            });
        }
    }



    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private DateTime _previewUntil;
    private int _previewFps = 20; 

    private void TriggerPreview(TimeSpan duration)
    {
        if (Settings.Default.StreamMode != 1)
            return;
        var until = DateTime.UtcNow + duration;
        if (until > _previewUntil)
            _previewUntil = until;

        if (_previewTask is { IsCompleted: false })
            return;

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        _previewTask = Task.Run(() => PreviewLoopAsync(_previewCts.Token));
    }


    private async Task PreviewLoopAsync(CancellationToken token)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _previewFps);

        while (!token.IsCancellationRequested &&
               SwitchConnection?.Connected == true &&
               DateTime.UtcNow < _previewUntil)
        {
            var started = Stopwatch.StartNew();
            try
            {
                var pic = await SwitchConnection.Screengrab(SOUR?.Token ?? token).ConfigureAwait(false);
                if (pic is { Length: > 0 })
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _screenGrab = pic;
                        using var ms = new MemoryStream(pic);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        imgSwitchDisplay.Source = bitmap;
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
            }

            var remaining = frameInterval - started.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, token).ConfigureAwait(false);
        }
    }

    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    private void StartScreenStream(bool force = false)
    {
        if (!force && Settings.Default.StreamMode != 0)
            return;
        StopScreenStream();

        _streamCts = new CancellationTokenSource();
        _streamTask = Task.Run(() => ScreenLoopAsync(_streamCts.Token));
    }

    private void StopScreenStream()
    {
        try { _streamCts?.Cancel(); } catch { }
        _streamTask = null;
        _streamCts = null;
    }

    private void StartRtspStream(string url)
    {
        if (Settings.Default.StreamMode != 2)
            return;
        StopRtspStream();

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("RTSP地址不能为空！");
            return;
        }

        try
        {
            VlcView.Visibility = Visibility.Visible;
            imgSwitchDisplay.Visibility = Visibility.Collapsed; // 隐藏抓屏图层
            _rtspStreaming = true;

            var m = new Media(_libVLC!, new Uri(url),
                $":network-caching={Settings.Default.RtspCache}",  // 网络缓冲（150ms左右）
                ":live-caching=0",       // 实时流额外缓冲禁用
                ":clock-jitter=0",       // 不做时钟抖动补偿
                ":clock-synchro=0",      // 不做音视频同步
                ":no-audio"              // 🚫 彻底禁用音频解码
            );
            _mediaPlayer!.Play(m);
        }
        catch (Exception ex)
        {
            _rtspStreaming = false;
            Dispatcher.Invoke(() =>
            {
                VlcView.Visibility = Visibility.Collapsed;
                imgSwitchDisplay.Visibility = Visibility.Visible;
                ellipseStatus.Fill = Brushes.Yellow;
                txtStatus.Text = "RTSP 未连接";
            });
            MessageBox.Show($"RTSP连接失败：{ex.Message}");
        }
    }

    private void StopRtspStream()
    {
        try { _mediaPlayer?.Stop(); } catch { }
        VlcView.Visibility = Visibility.Collapsed;
        imgSwitchDisplay.Visibility = Visibility.Visible;
    }

    private async Task ScreenLoopAsync(CancellationToken token)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / Settings.Default.RtspCache);

        while (!token.IsCancellationRequested && SwitchConnection?.Connected == true && !_rtspStreaming)
        {
            var started = Stopwatch.StartNew();
            try
            {
                var pic = await SwitchConnection.Screengrab(SOUR?.Token ?? token).ConfigureAwait(false);
                if (pic is { Length: > 0 })
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _screenGrab = pic;
                        using var ms = new MemoryStream(pic);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        imgSwitchDisplay.Source = bitmap;
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
            }

            var remaining = frameInterval - started.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, token).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// 点击按钮
    /// </summary>
    /// <param name="b"></param>
    /// <returns></returns>
    public async Task Click(SwitchButton b)
    {
        if (CON != null && SwitchConnection.Connected)
        {
            await CON.SendAsync(SwitchCommand.Click(b), SOUR.Token).ConfigureAwait(false);
            TriggerPreview(TimeSpan.FromSeconds(2));
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.Text = $"暂未连接switch，请在连接完成后再使用！！";
            });
        }
    }

    /// <summary>
    /// 按下按钮并保持
    /// </summary>
    /// <param name="b"></param>
    /// <returns></returns>
    public async Task HoldDown(SwitchButton b)
    {
        if (CON != null && SwitchConnection.Connected)
        {
            await CON.SendAsync(SwitchCommand.Hold(b), SOUR.Token).ConfigureAwait(false);
            TriggerPreview(TimeSpan.FromSeconds(2));
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.Text = $"暂未连接switch，请在连接完成后再使用！！";
            });
        }
    }

    /// <summary>
    /// 松开按钮
    /// </summary>
    /// <param name="b"></param>
    /// <returns></returns>
    public async Task HoldUp(SwitchButton b)
    {
        if (CON != null && SwitchConnection.Connected)
        {
            await CON.SendAsync(SwitchCommand.Release(b), SOUR.Token).ConfigureAwait(false);
            TriggerPreview(TimeSpan.FromSeconds(2));
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.Text = $"暂未连接switch，请在连接完成后再使用！！";
            });
        }
    }

    /// <summary>
    /// 设置摇杆
    /// </summary>
    /// <param name="stick"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public async Task SetStick(SwitchStick stick, short x, short y)
    {
        if (SwitchConnection != null && SwitchConnection.Connected)
        {
            var cmd = SwitchCommand.SetStick(stick, x, y, true);
            await SwitchConnection.SendAsync(cmd, SOUR.Token).ConfigureAwait(false);
            TriggerPreview(TimeSpan.FromSeconds(1));
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.Text = $"暂未连接switch，请在连接完成后再使用！！";
            });
        }
    }

    /// <summary>
    /// 按下 Switch 按钮
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private async void Btn_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn)
            return;
        SwitchButton? b = btn.Name switch
        {
            "btnUp" => DUP,
            "btnDown" => DDOWN,
            "btnLeft" => DLEFT,
            "btnRight" => DRIGHT,
            "btnZL" => ZL,
            "btnL" => L,
            "btnMinus" => MINUS,
            "btnCaptureLeft" => CAPTURE,
            "btnX" => X,
            "btnY" => Y,
            "btnA" => A,
            "btnB" => B,
            "btnZR" => ZR,
            "btnR" => R,
            "btnPlus" => PLUS,
            "btnHome" => HOME,
            _ => null
        };
        if (b == null)
            return;
        isHolding = true;
        holdToken = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            await Task.Delay(400); // 按下400ms后算长按
            if (isHolding)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await HoldDown(b.Value);
                });
            }
        });

        await Click(b.Value);
    }

    /// <summary>
    /// 松开 Switch 按钮
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private async void Btn_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn)
            return;
        SwitchButton? b = btn.Name switch
        {
            "btnUp" => DUP,
            "btnDown" => DDOWN,
            "btnLeft" => DLEFT,
            "btnRight" => DRIGHT,
            "btnZL" => ZL,
            "btnL" => L,
            "btnMinus" => MINUS,
            "btnCaptureLeft" => CAPTURE,
            "btnX" => X,
            "btnY" => Y,
            "btnA" => A,
            "btnB" => B,
            "btnZR" => ZR,
            "btnR" => R,
            "btnPlus" => PLUS,
            "btnHome" => HOME,
            _ => null
        };
        if (b == null)
            return;
        isHolding = false;

        // 取消长按
        holdToken?.Cancel();

        await HoldUp(b.Value);
    }

    private void btnSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow();
        win.Owner = this;
        if (win.ShowDialog() == true)
        {
            txtIpAddress.Text = Settings.Default.SwitchIP;
            txtPort.Text = Settings.Default.SwitchPort.ToString();

            txtLog.Text = "设置已更新。";
        }
    }

    private void btnStopStream_Click(object sender, RoutedEventArgs e)
    {
        btnStopStream.IsEnabled = false;
        btnCapture.IsEnabled = false;
        btnStartStream.IsEnabled = true;
        if (SwitchConnection.Connected)
            SwitchConnection.Disconnect();
        Source.Cancel();
        Source = new CancellationTokenSource();
        SOUR = Source;
        StopRtspStream();
        Dispatcher.Invoke(() =>
        {
            txtLog.Text = "Switch | 已断开连接！！";
            ellipseStatus.Fill = Brushes.Red;
            txtStatus.Text = "未连接";
        });
    }

    private async void btnCapture_Click(object sender, RoutedEventArgs e)
    {
        if (!SwitchConnection.Connected || SOUR == null || SOUR.IsCancellationRequested)
        {
            MessageBox.Show("保存失败，你还未连接 Switch！");
            return;
        }

        try
        {
            var saveFileDialog = new SaveFileDialog
            {
                Title = "保存截图",
                Filter = "PNG 图片|*.png",
                FileName = "Switch_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + ".png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (saveFileDialog.ShowDialog() != true)
                return;

            string filePath = saveFileDialog.FileName;

            _screenGrab = _screenGrab.Length == 0
                        ? await SwitchConnection.Screengrab(SOUR.Token).ConfigureAwait(false) ?? []
                        : _screenGrab;

            if (_screenGrab.Length == 0)
            {
                Dispatcher.Invoke(() => MessageBox.Show("截图数据为空，保存失败。"));
                return;
            }


            await File.WriteAllBytesAsync(filePath, _screenGrab);

            Dispatcher.Invoke(() =>
            {
                txtLog.Text = "截图已保存到：" + filePath;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("截图保存失败：" + ex.Message);
                txtLog.Text = "截图保存失败。";
            });
        }
    }

    private void btnRtspRescue_Click(object sender, RoutedEventArgs e)
    {
        if (_rtspStreaming)
        {
            _rtspStreaming = false;
            VlcView.Visibility = Visibility.Collapsed;
            imgSwitchDisplay.Visibility = Visibility.Visible;
            btnRtspRescue.Content = "RTSP恢复";
            StartScreenStream(true);
        }
        else
        {
            _rtspStreaming = true;
            VlcView.Visibility = Visibility.Visible;
            imgSwitchDisplay.Visibility = Visibility.Collapsed;
            btnRtspRescue.Content = "RTSP救援";
        }
    }
}