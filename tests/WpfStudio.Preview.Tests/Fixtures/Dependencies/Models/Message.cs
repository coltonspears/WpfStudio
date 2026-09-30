using CommunityToolkit.Mvvm.ComponentModel;
namespace PreviewDependency.Models;
public sealed class Message : ObservableObject
{
    public string Text => "Transitive package loaded";
}
