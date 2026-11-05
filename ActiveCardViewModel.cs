using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace RobloxWorker.Launcher.Models;

/// <summary>
/// Карточка активного окна Roblox на главной вкладке.
/// </summary>
public sealed class ActiveCardViewModel : INotifyPropertyChanged
{
    private string _username = string.Empty;
    private string _statusText = string.Empty;
    private Brush _statusColor = Brushes.Gray;
    private string _detailText = string.Empty;
    private string _timerText = "00:00:00";
    private string _jobId = "—";
    private double _progressValue;

    public long UserId { get; init; }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public Brush StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    public string DetailText
    {
        get => _detailText;
        set => SetField(ref _detailText, value);
    }

    public string TimerText
    {
        get => _timerText;
        set => SetField(ref _timerText, value);
    }

    public string JobId
    {
        get => _jobId;
        set => SetField(ref _jobId, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetField(ref _progressValue, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
