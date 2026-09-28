using System.Windows.Controls;

namespace WpfStudio.PreviewFixture;

public sealed class FailureView : UserControl
{
    public FailureView() => throw new InvalidOperationException("Deliberate compiled constructor failure");
}
