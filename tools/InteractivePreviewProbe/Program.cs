using System.IO;
using System.Windows;

namespace InteractivePreviewProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (options.Child)
        {
            var child = new ChildProbe(app, options);
            app.Startup += (_, _) => child.Start();
            app.Exit += (_, _) => child.Dispose();
            return app.Run();
        }
        using var parent = new ParentProbe(app, options);
        return app.Run(parent.Window);
    }
}

internal sealed record Options(bool Child, bool Automated, string Surface, string? Pipe,
    string? Token, int ParentPid, string LogPath)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        string surface = Value("--surface") ?? "child";
        if (surface is not ("child" or "window")) throw new ArgumentException("--surface must be child or window.");
        return new(args.Contains("--child"), args.Contains("--auto-message-smoke"), surface,
            Value("--pipe"), Value("--token"), int.TryParse(Value("--parent"), out int pid) ? pid : 0,
            Path.GetFullPath(Value("--log") ?? Path.Combine(AppContext.BaseDirectory, "probe.jsonl")));
    }
}
