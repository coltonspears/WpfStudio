using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Inspection;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    public InspectionViewModel LiveInspection { get; }
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task RunWithInspectionAsync(CancellationToken cancel) => BuildAndLaunch.LaunchWithInspectionAsync(false, cancel);
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRunWorkspaceOperation))] private Task DebugWithInspectionAsync(CancellationToken cancel) => BuildAndLaunch.LaunchWithInspectionAsync(true, cancel);
}
