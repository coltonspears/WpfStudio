using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using System.Threading.Tasks;

namespace CounterApp;

public partial class CounterViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int Count { get; set; }

    [RelayCommand]
    private void Increment()
    {
        Count++; // Set a breakpoint here, press F5, and click Increment.
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task IncrementLaterAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(200, cancellationToken);
        Count++; // Test stepping after an async continuation.
    }
}
