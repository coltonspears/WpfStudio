using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.ViewModels;

/// <summary>The saved connections and the form that edits them. The form is also what a query runs against.</summary>
public partial class ConnectionProfilesViewModel : ObservableObject
{
    private readonly IConnectionProfileStore _profiles;
    private readonly Action<string> _report;
    public ConnectionProfilesViewModel(IConnectionProfileStore profiles, Action<string> report)
    {
        _profiles = profiles; _report = report;
    }
    /// <summary>The form now shows a saved connection, or a new one when null.</summary>
    public event Action<ConnectionProfile?>? ConnectionChanged;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    [ObservableProperty] public partial ConnectionProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "Local SQL Server";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveTarget))] public partial string Server { get; set; } = "localhost";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveTarget))] public partial string DatabaseName { get; set; } = "master";
    [ObservableProperty] public partial bool WindowsAuthentication { get; set; } = true;
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool RememberPassword { get; set; }
    [ObservableProperty] public partial bool TrustServerCertificate { get; set; }
    public string ActiveTarget => $"{Server}  /  {DatabaseName}";

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is null) return;
        ProfileName = value.Name; Server = value.Server; DatabaseName = value.Database; WindowsAuthentication = value.WindowsAuthentication;
        UserName = value.UserName; Password = _profiles.UnlockPassword(value); RememberPassword = value.RememberPassword; TrustServerCertificate = value.TrustServerCertificate;
        ConnectionChanged?.Invoke(value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (ConnectionProfile profile in await _profiles.LoadAsync(cancellationToken)) Profiles.Add(profile);
            SelectedProfile = Profiles.FirstOrDefault();
        }
        catch (Exception exception) { _report("Could not load saved connections: " + exception.Message); }
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null; ProfileName = "New connection"; Server = "localhost"; DatabaseName = "master";
        WindowsAuthentication = true; UserName = ""; Password = ""; RememberPassword = false; TrustServerCertificate = false;
        ConnectionChanged?.Invoke(null);
    }

    [RelayCommand]
    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConnectionProfile profile = CurrentProfile();
            if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Enter a connection name.");
            _ = SqlDatabaseService.BuildConnectionString(profile);
            List<ConnectionProfile> updated = Profiles.Where(item => item.Id != profile.Id).Append(profile).ToList();
            await _profiles.SaveAsync(updated, cancellationToken);
            Profiles.Clear(); foreach (ConnectionProfile item in updated) Profiles.Add(item);
            SelectedProfile = profile; _report("Connection saved. Password is protected for this Windows user when Remember is enabled.");
        }
        catch (Exception exception) { _report(exception.Message); }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null) return;
        try
        {
            var remaining = Profiles.Where(profile => profile.Id != SelectedProfile.Id).ToList();
            await _profiles.SaveAsync(remaining, cancellationToken);
            Profiles.Clear(); foreach (ConnectionProfile profile in remaining) Profiles.Add(profile);
            SelectedProfile = remaining.FirstOrDefault(); _report("Saved connection removed.");
        }
        catch (Exception exception) { _report(exception.Message); }
    }

    public ConnectionProfile CurrentProfile() => new()
    {
        Id = SelectedProfile?.Id ?? Guid.NewGuid(), Name = ProfileName, Server = Server, Database = DatabaseName,
        WindowsAuthentication = WindowsAuthentication, UserName = UserName, Password = Password,
        RememberPassword = RememberPassword, TrustServerCertificate = TrustServerCertificate
    };

    /// <summary>Points the form at the server a schema tree came from. The database is the caller's choice.</summary>
    public void UseServerOf(ConnectionProfile source)
    {
        Server = source.Server; WindowsAuthentication = source.WindowsAuthentication;
        UserName = source.UserName; Password = source.Password; TrustServerCertificate = source.TrustServerCertificate;
    }
}
