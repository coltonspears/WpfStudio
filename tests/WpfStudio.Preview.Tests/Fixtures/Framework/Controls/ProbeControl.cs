using System;
using System.Configuration;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace WpfStudio.PreviewFrameworkControls
{
    /// <summary>Reports what project code sees of the process it runs in, as one line of key=value facts.</summary>
    public class ProbeControl : TextBlock
    {
        // Libraries of the host's transport, which the project's own copies must never have to compete with.
        private static readonly string[] Transport = { "StreamJsonRpc", "MessagePack", "Nerdbank.Streams", "Microsoft.VisualStudio.Threading", "System.Text.Json", "System.Memory", "System.Reflection.Metadata", "Microsoft.Bcl.AsyncInterfaces" };

        public ProbeControl()
        {
            Report();
            Loaded += (_, _) => Report();
        }

        private void Report()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToArray();
            Text = string.Join("|", new[]
            {
                "runtime=" + RuntimeInformation.FrameworkDescription,
                "bits=" + IntPtr.Size * 8,
                // The property that only .NET Framework has, which DevExpress reads while initializing its themes.
                "manager=" + AppDomain.CurrentDomain.SetupInformation.AppDomainManagerType,
                "base=" + AppDomain.CurrentDomain.BaseDirectory,
                "config=" + ConfigurationManager.AppSettings["FixtureValue"],
                "entry=" + Assembly.GetEntryAssembly()?.GetName().Name,
                "json=" + typeof(JsonConvert).Assembly.GetName().Version + JsonConvert.SerializeObject(new { a = 1 }),
                "transport=" + string.Join(",", Transport.Intersect(loaded)),
            });
        }
    }
}
