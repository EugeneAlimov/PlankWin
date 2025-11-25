using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PlankWin;

public class DockAppConfig : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string? _arguments;
    private string? _icon;
    private string? _workingDirectory;
    private bool _runAsAdmin;

    private bool _isDynamic;
    private string? _processName;
    private bool _isRunning;
    private int _processId;

    private IntPtr _windowHandle;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    public string? Arguments
    {
        get => _arguments;
        set => SetField(ref _arguments, value);
    }

    public string? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public string? WorkingDirectory
    {
        get => _workingDirectory;
        set => SetField(ref _workingDirectory, value);
    }

    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set => SetField(ref _runAsAdmin, value);
    }

    // Динамическое приложение (создано по процессу во время работы)
    [JsonIgnore]
    public bool IsDynamic
    {
        get => _isDynamic;
        set => SetField(ref _isDynamic, value);
    }

    // Имя процесса (может отличаться от имени exe)
    public string? ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    // Запущено ли приложение (для подсветки)
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    [JsonIgnore]
    public int ProcessId
    {
        get => _processId;
        set => SetField(ref _processId, value);
    }

    // Хэндл окна (для динамических приложений), нужен для вытаскивания иконки окна
    [JsonIgnore]
    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set => SetField(ref _windowHandle, value);
    }

    // Для отладки
    public override string ToString() => $"{Name} ({Path})";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
