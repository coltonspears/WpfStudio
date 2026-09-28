using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace InteractivePreviewProbe;

internal sealed class ChildProbe(Application app, Options options) : IDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private Wire? _wire;
    private HwndSource? _source;
    private Window? _window;
    private Grid? _root;
    private TextBox? _input;
    private Button? _button;
    private ComboBox? _combo;
    private Popup? _popup;
    private ContextMenu? _menu;
    private nint _parent;
    private int _clicks, _nativeMessages;

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            if (options.Pipe is null || options.Token is null || options.ParentPid <= 0)
                throw new InvalidOperationException("Missing child session arguments.");
            using var pipe = new NamedPipeClientStream(".", options.Pipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10000, _closed.Token).ConfigureAwait(false);
            Native.Require(Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server), "Get pipe server PID");
            if (server != options.ParentPid) throw new InvalidOperationException("Pipe server is not the launching parent.");
            _wire = new Wire(pipe);
            Task writer = _wire.WriteLoopAsync();
            _wire.Send(new("hello", Number: Environment.ProcessId, Token: options.Token));
            while (!_closed.IsCancellationRequested)
            {
                Packet packet = await _wire.ReadAsync(_closed.Token).ConfigureAwait(false);
                if (packet.Token != options.Token) throw new InvalidDataException("Wrong session token.");
                // Never block the I/O thread waiting for application code. The parent's watchdog
                // observes dispatcher acknowledgements and can terminate this process independently.
                _ = app.Dispatcher.BeginInvoke(() => Dispatch(packet));
            }
            await writer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            _ = app.Dispatcher.BeginInvoke(() => app.Shutdown(1));
        }
    }

    private void Dispatch(Packet packet)
    {
        try
        {
            switch (packet.Kind)
            {
                case "attach": Attach(packet); break;
                case "ping": Send("pong", packet.Id); break;
                case "state": SendState(packet.Id); break;
                case "size": Resize(packet.Width, packet.Height); SendState(packet.Id); break;
                case "post-text":
                    foreach (char ch in (packet.Text ?? "").Take(128))
                        Native.Require(Native.PostMessage(OwnedSurfaceHandle(), Native.Character, new nint(ch), new nint(1)), "Post owned WM_CHAR");
                    break;
                case "post-tab":
                    Native.Require(Native.PostMessage(OwnedSurfaceHandle(), Native.KeyDown, new nint(9), new nint(1)), "Post owned Tab keydown");
                    Native.Require(Native.PostMessage(OwnedSurfaceHandle(), Native.KeyUp, new nint(9), new nint(unchecked((int)0xC0000001))), "Post owned Tab keyup");
                    break;
                case "post-click":
                    PixelPoint point = ButtonPoint();
                    int packed = (point.Y << 16) | (point.X & 0xffff);
                    Native.Require(Native.PostMessage(OwnedSurfaceHandle(), Native.MouseMove, 0, new nint(packed)), "Post owned WM_MOUSEMOVE");
                    Native.Require(Native.PostMessage(OwnedSurfaceHandle(), Native.MouseDown, new nint(1), new nint(packed)), "Post owned WM_LBUTTONDOWN");
                    Native.Require(Native.PostMessage(OwnedSurfaceHandle(), Native.MouseUp, 0, new nint(packed)), "Post owned WM_LBUTTONUP");
                    break;
                case "focus":
                    if (_source is not null)
                    {
                        var direction = packet.Text == "Last" ? FocusNavigationDirection.Last : FocusNavigationDirection.First;
                        bool focused = ((IKeyboardInputSink)_source).TabInto(new TraversalRequest(direction));
                        Send("focus-result", packet.Id, focused.ToString());
                        SendState(packet.Id);
                    }
                    break;
                case "popup":
                    if (_popup is not null) _popup.IsOpen = packet.Number != 0;
                    app.Dispatcher.BeginInvoke(() => SendState(packet.Id));
                    break;
                case "hide":
                    ClosePopups();
                    if (_source is not null) Native.ShowWindow(_source.Handle, 0);
                    SendState(packet.Id);
                    break;
                case "show":
                    if (_source is not null) Native.ShowWindow(_source.Handle, 4);
                    SendState(packet.Id);
                    break;
                case "hang":
                    Send("hang-entered", packet.Id, "Dispatcher intentionally blocked; watchdog must kill this owned process.");
                    Thread.Sleep(Timeout.Infinite);
                    break;
                case "stop":
                    ClosePopups();
                    Send("stopped", packet.Id);
                    app.Shutdown();
                    break;
            }
        }
        catch (Exception ex)
        {
            Send("error", packet.Id, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void Attach(Packet packet)
    {
        if (_source is not null) throw new InvalidOperationException("This probe permits one surface per process.");
        _parent = new nint(packet.Handle);
        Native.RequireWindow(_parent, options.ParentPid);
        if (Native.GetProp(_parent, Native.LeaseProperty(options.Token!)) != new nint(1))
            throw new InvalidOperationException("Bridge lease is absent or stale.");
        Native.RequireDpi(_parent);
        _root = CreateContent();
        if (options.Surface == "window")
        {
            // Deliberately preserve an actual Window and its logical ancestry. This branch
            // probes whether WPF's top-level assumptions tolerate native reparenting.
            _window = new Window
            {
                Content = _root, Width = 620, Height = 330, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.Manual,
                Title = "Owned probe Window"
            };
            _window.Resources["WindowAncestorValue"] = "Retained Window resources";
            _window.Show();
            nint hwnd = new WindowInteropHelper(_window).Handle;
            Native.ShowWindow(hwnd, 0);
            Native.RequireDpi(hwnd);
            long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
            style = (style & ~(0x80000000L | 0x00C00000L | 0x00040000L)) | Native.Child | Native.ClipChildren;
            Native.SetWindowLongPtr(hwnd, -16, new nint(style));
            Marshal.SetLastPInvokeError(0);
            nint oldParent = Native.SetParent(hwnd, _parent);
            if (oldParent == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Parent actual Window HWND");
            _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Window source is absent.");
            Send("window-ancestry", text: $"Window.GetWindow(root) preserved: {ReferenceEquals(Window.GetWindow(_root), _window)}");
        }
        else
        {
            var parameters = new HwndSourceParameters("Interactive preview probe child")
            {
                ParentWindow = _parent, WindowStyle = Native.Child | Native.Visible | Native.ClipChildren | Native.ClipSiblings,
                Width = Math.Max(1, packet.Width), Height = Math.Max(1, packet.Height),
                PositionX = 0, PositionY = 0, TreatAsInputRoot = true
            };
            _source = new HwndSource(parameters) { RootVisual = _root };
        }
        Native.RequireWindow(_source.Handle, Environment.ProcessId);
        if (Native.GetParent(_source.Handle) != _parent) throw new InvalidOperationException("Unexpected surface parent.");
        if (!Native.AreDpiAwarenessContextsEqual(Native.GetWindowDpiAwarenessContext(_source.Handle), Native.GetWindowDpiAwarenessContext(_parent)))
            throw new InvalidOperationException("Actual parent/child DPI awareness mismatch after creation.");
        _source.AddHook(ObserveNativeMessage);
        ((IKeyboardInputSink)_source).KeyboardInputSite = new RemoteSite((IKeyboardInputSink)_source, direction =>
            Send("tab-out", text: direction.ToString()));
        Resize(packet.Width, packet.Height);
        Native.ShowWindow(_source.Handle, 4); // SW_SHOWNOACTIVATE, never foreground activation.
        Send("attached", handle: _source.Handle.ToInt64());
        app.Dispatcher.BeginInvoke(() => SendState(packet.Id));
    }

    private Grid CreateContent()
    {
        var root = new Grid { Background = Brushes.AliceBlue, Margin = new Thickness(0) };
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Continue);
        var stack = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(stack);
        stack.Children.Add(new TextBlock { Text = "Separate process: real WPF controls", FontWeight = FontWeights.Bold });
        _input = new TextBox { Name = "NativeTextInput", Text = "", Margin = new Thickness(0, 10, 0, 10), Height = 30 };
        _input.TextChanged += (_, _) => Send("text-changed", text: _input.Text);
        stack.Children.Add(_input);
        _button = new Button { Name = "NativeClick", Content = "Native click counter: 0", Height = 34, Margin = new Thickness(0, 0, 0, 10) };
        _button.Click += (_, _) => { _clicks++; _button.Content = $"Native click counter: {_clicks}"; Send("button-click", text: _clicks.ToString()); };
        stack.Children.Add(_button);
        _combo = new ComboBox { Name = "NativeCombo", ItemsSource = new[] { "First", "Second", "Third" }, SelectedIndex = 0, Height = 30 };
        _combo.DropDownOpened += (_, _) => Send("combo-opened");
        _combo.DropDownClosed += (_, _) => Send("combo-closed");
        stack.Children.Add(_combo);
        var slider = new Slider { Name = "NativeDrag", Minimum = 0, Maximum = 100, Value = 20, Margin = new Thickness(0, 10, 0, 8) };
        slider.ValueChanged += (_, _) => Send("slider-changed", text: slider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        stack.Children.Add(slider);
        var popupButton = new Button { Content = "Open separate popup HWND", Height = 30 };
        _popup = new Popup
        {
            PlacementTarget = popupButton, Placement = PlacementMode.Bottom,
            StaysOpen = options.Automated, AllowsTransparency = options.Automated,
            Child = new Border { Background = Brushes.LightGoldenrodYellow, Padding = new Thickness(16),
                Opacity = options.Automated ? 0 : 1, IsHitTestVisible = !options.Automated,
                Child = new TextBox { Name = "PopupInput", Text = "Popup text entry", Width = 240 } }
        };
        _popup.Opened += (_, _) =>
        {
            // Popup placement can clamp even an offscreen owner back onto a monitor. Automated
            // mode creates only a fully transparent, noncapturing popup and hides it immediately;
            // this measures HWND lifecycle, not visible popup interaction.
            if (options.Automated && _popup.Child is Visual popupVisual && PresentationSource.FromVisual(popupVisual) is HwndSource popupSource)
                Native.ShowWindow(popupSource.Handle, 0);
            app.Dispatcher.BeginInvoke(() => SendState());
        };
        _popup.Closed += (_, _) => Send("popup-closed");
        popupButton.Click += (_, _) => _popup.IsOpen = true;
        stack.Children.Add(popupButton);
        var hang = new Button { Content = "Hang this child dispatcher (watchdog should recover)", Margin = new Thickness(0, 8, 0, 0), Height = 30 };
        hang.Click += (_, _) => Dispatch(new Packet("hang"));
        stack.Children.Add(hang);
        _menu = new ContextMenu();
        var item = new MenuItem { Header = "Context menu action" };
        item.Click += (_, _) => Send("context-menu-click");
        _menu.Items.Add(item);
        root.ContextMenu = _menu;
        root.PreviewKeyDown += (_, e) => Send("wpf-key", text: $"{e.Key}; modifiers={Keyboard.Modifiers}; focus={FocusName()}");
        root.GotKeyboardFocus += (_, _) => Send("wpf-focus", text: FocusName());
        root.ToolTip = "Remote WPF tooltip";
        return root;
    }

    private void Resize(int width, int height)
    {
        if (_source is null) return;
        Native.RequireWindow(_source.Handle, Environment.ProcessId);
        Native.RequireWindow(_parent, options.ParentPid);
        if (Native.GetParent(_source.Handle) != _parent || Native.GetProp(_parent, Native.LeaseProperty(options.Token!)) != new nint(1))
            throw new InvalidOperationException("Surface lease changed.");
        Native.Require(Native.SetWindowPos(_source.Handle, 0, 0, 0, Math.Clamp(width, 1, 8192), Math.Clamp(height, 1, 8192),
            Native.NoZOrder | Native.NoActivate | Native.FrameChanged), "Resize owned child surface");
        _root?.UpdateLayout();
    }

    private nint ObserveNativeMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x0200 or 0x0201 or 0x0202 or 0x0100 or 0x0101 or 0x0102 or 0x0007 or 0x0008 or 0x02E0)
        {
            _nativeMessages++;
            Send("native-message", text: $"0x{message:X}; wParam={wParam}; lParam={lParam}");
        }
        return 0;
    }

    private string FocusName() => Keyboard.FocusedElement is FrameworkElement element ? element.Name + "/" + element.GetType().Name : "none";
    private nint OwnedSurfaceHandle()
    {
        HwndSource source = _source ?? throw new InvalidOperationException("No current child surface.");
        source.Dispatcher.VerifyAccess();
        nint hwnd = source.Handle;
        Native.RequireWindow(hwnd, Environment.ProcessId);
        if (Native.GetParent(hwnd) != _parent || Native.GetProp(_parent, Native.LeaseProperty(options.Token!)) != new nint(1))
            throw new InvalidOperationException("Surface lease changed.");
        return hwnd;
    }

    private PixelPoint ButtonPoint()
    {
        if (_source?.RootVisual is not Visual root || _button is null)
            throw new InvalidOperationException("No current button visual.");
        _root?.UpdateLayout();
        Point point = _button.TransformToAncestor(root).Transform(new Point(_button.ActualWidth / 2, _button.ActualHeight / 2));
        if (_source.CompositionTarget is { } composition) point = composition.TransformToDevice.Transform(point);
        return new PixelPoint((int)point.X, (int)point.Y);
    }

    private void SendState(long id = 0)
    {
        if (_source is null || _root is null || _button is null) return;
        _root.UpdateLayout();
        PixelPoint point = ButtonPoint();
        nint popupHwnd = _popup?.Child is Visual visual ? (PresentationSource.FromVisual(visual) as HwndSource)?.Handle ?? 0 : 0;
        var state = new ProbeState(_clicks, _input?.Text ?? "", FocusName(), _popup?.IsOpen == true,
            popupHwnd.ToInt64(), _source.Handle.ToInt64(), Native.GetParent(_source.Handle).ToInt64(), Native.GetDpiForWindow(_source.Handle),
            true, point, _nativeMessages, _root.IsKeyboardFocusWithin);
        _wire?.Send(new("state", id, State: state, Token: options.Token));
    }

    private void Send(string kind, long id = 0, string? text = null, long handle = 0) =>
        _wire?.Send(new(kind, id, text, handle, Token: options.Token));
    private void ClosePopups()
    {
        if (_popup is not null) _popup.IsOpen = false;
        if (_combo is not null) _combo.IsDropDownOpen = false;
        if (_menu is not null) _menu.IsOpen = false;
        if (_root is not null) ToolTipService.SetIsEnabled(_root, false);
    }
    public void Dispose()
    {
        _closed.Cancel();
        ClosePopups();
        if (_source is not null) ((IKeyboardInputSink)_source).KeyboardInputSite = null;
        _window?.Close();
        _source?.Dispose();
        _wire?.Dispose();
    }

    private sealed class RemoteSite(IKeyboardInputSink sink, Action<FocusNavigationDirection> leave) : IKeyboardInputSite
    {
        public IKeyboardInputSink Sink => sink;
        public bool OnNoMoreTabStops(TraversalRequest request) { leave(request.FocusNavigationDirection); return true; }
        public void Unregister() { }
    }
}
