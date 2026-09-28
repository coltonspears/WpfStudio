using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;

namespace WpfStudio.InspectionFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = args[0];
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var primary = new PrimaryWindow();
        primary.Show();
        var secondary = CreateWindow("SecondaryWindow", "Inspection fixture secondary", "SecondaryText", out _);
        secondary.Show();
        var popupText = new TextBlock { Name = "PopupText", Text = "Live popup content", DataContext = new MissingValueSource() };
        popupText.SetBinding(FrameworkElement.TagProperty, new Binding("MissingPopupValue"));
        var popupPickButton = new Button { Name = "PopupPickButton", Content = "Popup application input", Width = 220, Height = 35, ClickMode = ClickMode.Press };
        var popupContent = new StackPanel();
        popupContent.Children.Add(popupText);
        popupContent.Children.Add(popupPickButton);
        var popup = new Popup
        {
            Name = "FixturePopup", PlacementTarget = primary, Placement = PlacementMode.Center,
            StaysOpen = true, Child = new AdornerDecorator { Child = popupContent }
        };
        popup.IsOpen = true;

        Dispatcher? childDispatcher = null;
        Button? childPickButton = null;
        using var childReady = new ManualResetEventSlim();
        var childThread = new Thread(() =>
        {
            childDispatcher = Dispatcher.CurrentDispatcher;
            var childWindow = CreateWindow("ChildDispatcherWindow", "Inspection fixture child dispatcher", "ChildDispatcherText", out childPickButton);
            childWindow.Show();
            childReady.Set();
            Dispatcher.Run();
            childWindow.Close();
        }) { IsBackground = true };
        childThread.SetApartmentState(ApartmentState.STA);
        childThread.Start();
        if (!childReady.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Child WPF dispatcher did not start.");
        var input = new InputFixture(directory, primary.PrimaryPickButton, childPickButton!, popupPickButton);
        var layout = new LayoutFixture(directory, primary.PrimaryPickButton, childPickButton!, popupPickButton);
        var editing = new EditingFixture(directory, primary, childPickButton!);
        var diagnostics = new DiagnosticFixture(directory, primary);
        var sourceValidation = new SourceValidationFixture(directory, primary);
        var appearance = new AppearanceFixture(directory, primary, childPickButton!);
        var bindingSources = new BindingSourceProbeFixture(directory, primary);

        File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId, RuntimeMajor = Environment.Version.Major,
            MainThreadId = Environment.CurrentManagedThreadId, ChildThreadId = childThread.ManagedThreadId,
            ExistingHookRan = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_EXISTING_HOOK_RAN"),
            ExistingHookCount = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_EXISTING_HOOK_COUNT"),
            ProfileValue = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_PROFILE_VALUE"),
            RemainingHooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS"),
            InspectionPipe = Environment.GetEnvironmentVariable("WPFSTUDIO_INSPECTION_PIPE"),
            InspectionToken = Environment.GetEnvironmentVariable("WPFSTUDIO_INSPECTION_TOKEN"),
            InspectionOwner = Environment.GetEnvironmentVariable("WPFSTUDIO_INSPECTION_OWNER_PID"),
            AgentPath = AppDomain.CurrentDomain.GetAssemblies()
                .SingleOrDefault(assembly => assembly.GetName().Name == "WpfStudio.Inspection.Agent")?.Location
        }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            var commandPath = Path.Combine(directory, "command.json");
            if (!File.Exists(commandPath)) return;
            string command;
            try { command = File.ReadAllText(commandPath); File.Delete(commandPath); }
            catch (IOException) { return; }
            var request = JsonSerializer.Deserialize<Command>(command)!;
            primary.ApplyBindingCommand(request.Action);
            input.Apply(request.Action);
            layout.Apply(request.Action);
            editing.Apply(request.Action);
            diagnostics.Apply(request.Action);
            sourceValidation.Apply(request.Action);
            appearance.Apply(request.Action);
            bindingSources.Apply(request.Action);
            if (request.Action == "change") ((RuntimeViewModel)primary.DataContext).DisplayName = "Updated while running";
            if (request.Action == "close-popup") popup.IsOpen = false;
            if (request.Action == "break") BreakpointTarget();
            var acknowledgement = Path.Combine(directory, request.Id + ".ack");
            File.WriteAllText(acknowledgement + ".tmp", request.Action);
            File.Move(acknowledgement + ".tmp", acknowledgement);
            if (request.Action != "shutdown") return;
            timer.Stop();
            popup.IsOpen = false;
            childDispatcher!.BeginInvokeShutdown(DispatcherPriority.Normal);
            app.Shutdown();
        };
        timer.Start();
        app.Run();
        childThread.Join(TimeSpan.FromSeconds(5));
    }

    private static Window CreateWindow(string name, string title, string textName, out Button pickButton)
    {
        var text = new TextBlock { Name = textName, Text = title, DataContext = new MissingValueSource() };
        text.SetBinding(FrameworkElement.TagProperty, new Binding("MissingGeneratedValue"));
        pickButton = new Button { Name = name == "ChildDispatcherWindow" ? "ChildPickButton" : "SecondaryPickButton",
            Content = "Application input", Width = 180, Height = 35, ClickMode = ClickMode.Press };
        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(pickButton);
        return new()
        {
            Name = name, Title = title, Left = -32000, Top = -32000, Width = 250, Height = 180,
            ShowActivated = false, ShowInTaskbar = false, Content = content
        };
    }

    private static void BreakpointTarget()
    {
        int answer = 42;
        Console.WriteLine(answer); // INSPECTION_BREAKPOINT
    }

    private sealed record Command(string Id, string Action);
}
