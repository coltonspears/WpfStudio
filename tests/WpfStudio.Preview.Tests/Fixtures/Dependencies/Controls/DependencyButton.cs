using System.Windows.Controls;
using System.Windows.Markup;
using PreviewDependency.Models;
[assembly: XmlnsDefinition("urn:wpfstudio:dependency-controls", "PreviewDependency.Controls")]
namespace PreviewDependency.Controls;
public sealed class DependencyButton : Button
{
    public DependencyButton() => Content = new Message().Text;
}
