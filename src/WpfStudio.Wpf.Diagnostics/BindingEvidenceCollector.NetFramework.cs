#if NETFRAMEWORK
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>WPF on .NET Framework has no BindingDiagnostics.BindingFailed event, so there is no evidence to collect. Binding status and the trace log still report the failure.</summary>
public sealed class BindingEvidenceCollector : IDisposable
{
    /// <param name="ignore">Taken so both runtimes construct the collector the same way.</param>
    public BindingEvidenceCollector(Func<BindingExpressionBase, bool>? ignore = null) => _ = ignore;
    public void Clear() { }
    public (IReadOnlyList<InspectionBindingEvidence> Events, bool Truncated) Read(BindingExpressionBase binding) => ([], false);
    public void Dispose() { }
}
#endif
