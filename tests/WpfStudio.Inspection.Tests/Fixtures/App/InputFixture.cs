using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace WpfStudio.InspectionFixture;

/// <summary>Exercises WPF's real input staging and routed handlers without moving the user's pointer.</summary>
internal sealed class InputFixture
{
    private readonly string _directory;
    private readonly Button[] _targets;
    private readonly int[] _pointerDowns = new int[3];
    private readonly int[] _pointerUps = new int[3];
    private readonly int[] _clicks = new int[3];
    private readonly int[] _keys = new int[3];

    public InputFixture(string directory, Button main, Button child, Button popup)
    {
        _directory = directory;
        _targets = [main, child, popup];
        for (var index = 0; index < _targets.Length; index++)
        {
            var captured = index;
            var target = _targets[index];
            target.Dispatcher.Invoke(() =>
            {
                target.PreviewMouseDown += (_, _) => Interlocked.Increment(ref _pointerDowns[captured]);
                target.PreviewMouseUp += (_, _) => Interlocked.Increment(ref _pointerUps[captured]);
                target.PreviewKeyDown += (_, _) => Interlocked.Increment(ref _keys[captured]);
                target.Click += (_, _) => Interlocked.Increment(ref _clicks[captured]);
            });
        }
        WriteState();
    }

    public void Apply(string command)
    {
        if (!command.StartsWith("input-", StringComparison.Ordinal)) return;
        var parts = command.Split('-');
        if (parts.Length == 3)
        {
            int index = parts[1] switch { "main" => 0, "child" => 1, "popup" => 2, _ => -1 };
            if (index >= 0)
            {
                var target = _targets[index];
                target.Dispatcher.Invoke(() =>
                {
                    if (parts[2] == "escape")
                    {
                        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("Target has no presentation source.");
                        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = target });
                    }
                    else if (parts[2] == "move")
                    {
                        InputManager.Current.ProcessInput(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        { RoutedEvent = Mouse.PreviewMouseMoveEvent, Source = target });
                    }
                    else
                    {
                        InputManager.Current.ProcessInput(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = parts[2] == "down" ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent, Source = target });
                    }
                });
            }
        }
        WriteState();
    }

    private void WriteState()
    {
        var state = _targets.Select((target, index) => target.Dispatcher.Invoke(() => new
        {
            Name = target.Name,
            PointerDowns = Volatile.Read(ref _pointerDowns[index]),
            PointerUps = Volatile.Read(ref _pointerUps[index]),
            Clicks = Volatile.Read(ref _clicks[index]),
            Keys = Volatile.Read(ref _keys[index]),
            Adorners = AdornerLayer.GetAdornerLayer(target)?.GetAdorners(target)?.Length ?? 0
        })).ToArray();
        var path = Path.Combine(_directory, "input-state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
