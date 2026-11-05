using System.IO;
using System.Threading;
using System.Windows;

namespace WpfStudio.PreviewFrameworkControls
{
    /// <summary>Blocks the preview's dispatcher for good, once it has said so through a marker file.</summary>
    public class HangingControl : FrameworkElement
    {
        public static readonly DependencyProperty MarkerPathProperty = DependencyProperty.Register(nameof(MarkerPath), typeof(string), typeof(HangingControl),
            new PropertyMetadata(null, (_, args) =>
            {
                File.WriteAllText((string)args.NewValue, "entered");
                Thread.Sleep(Timeout.Infinite);
            }));

        public string MarkerPath { get => (string)GetValue(MarkerPathProperty); set => SetValue(MarkerPathProperty, value); }
    }
}
