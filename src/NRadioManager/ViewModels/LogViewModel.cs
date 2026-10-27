using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>
/// 通信日志页面：直接绑定 ActivityLog 的同一份内存数据源（该数据源同时持久化到磁盘），
/// 支持按关键字、类别、级别筛选，并显示当前日志文件与写入错误。
/// </summary>
public sealed class LogViewModel : ModuleViewModel
{
    private readonly ActivityLog _source;
    private string _searchText = "";
    private OptionItem? _selectedCategory;
    private OptionItem? _selectedLevel;
    private int _filteredCount;

    public LogViewModel(MainViewModel host, ActivityLog? source = null)
        : base(host, "通信日志", "本软件的运行事件与通信记录")
    {
        _source = source ?? host.Log;
        CategoryOptions = new ObservableCollection<OptionItem>
        {
            new("all", "全部类别"),
            new(nameof(LogCategory.Notification), "通知"),
            new(nameof(LogCategory.Connection), "连接"),
            new(nameof(LogCategory.Device), "设备"),
            new(nameof(LogCategory.Communication), "通信"),
            new(nameof(LogCategory.Sampling), "采样"),
            new(nameof(LogCategory.Network), "网络"),
            new(nameof(LogCategory.Application), "运行"),
            new(nameof(LogCategory.Configuration), "配置"),
            new(nameof(LogCategory.Data), "数据"),
            new(nameof(LogCategory.Persistence), "存储"),
            new(nameof(LogCategory.Other), "其他"),
        };
        LevelOptions = new ObservableCollection<OptionItem>
        {
            new("all", "全部级别"),
            new(nameof(LogLevel.Info), "信息"),
            new(nameof(LogLevel.Success), "成功"),
            new(nameof(LogLevel.Warning), "警告"),
            new(nameof(LogLevel.Error), "错误"),
        };
        _selectedCategory = CategoryOptions[0];
        _selectedLevel = LevelOptions[0];

        FilteredItems = new ObservableCollection<LogItem>();
        _source.Items.CollectionChanged += OnSourceCollectionChanged;
        _source.PropertyChanged += OnLogPropertyChanged;

        ClearCommand = new RelayCommand(ClearLog);
        OpenFolderCommand = new RelayCommand(OpenLogFolder);
        RefreshCommand = new RelayCommand(RefreshFilter);
        RefreshFilter();
    }

    public ObservableCollection<LogItem> Items => _source.Items;
    public ObservableCollection<LogItem> FilteredItems { get; }
    public ObservableCollection<OptionItem> CategoryOptions { get; }
    public ObservableCollection<OptionItem> LevelOptions { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            RefreshFilter();
        }
    }

    public OptionItem? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            RefreshFilter();
        }
    }

    public OptionItem? SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (!Set(ref _selectedLevel, value)) return;
            RefreshFilter();
        }
    }

    public int FilteredCount
    {
        get => _filteredCount;
        private set
        {
            if (!Set(ref _filteredCount, value)) return;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => FilteredCount == 0;
    public string CountText =>
        $"显示 {FilteredCount} / 内存中 {_source.Items.Count} 条，本次启动已加载 {_source.LoadedCount} 条" +
        (_source.DroppedCount > 0 ? $"，队列满时丢弃 {_source.DroppedCount} 条" : "");
    public string LogFilePath => _source.CurrentFilePath;
    public string LogDirectory => _source.DirectoryPath;
    public string? LoadWarning => _source.LoadWarning;
    public bool HasLoadWarning => !string.IsNullOrWhiteSpace(LoadWarning);
    public string StorageError => _source.LastWriteError ?? "";
    public bool HasStorageError => _source.HasWriteError;
    public int DroppedCount => _source.DroppedCount;

    public ICommand ClearCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public string LogNote =>
        "日志会持续写入本地 activity.log，并按大小轮转到 activity.1.log 等归档，默认保留 30 天。" +
        "密码、Cookie、Digest nonce/response、Authorization 等敏感内容在写入前统一打码；" +
        "成功轮询按 10 分钟节流记录，状态变化和异常不省略。";

    public string RetentionNote =>
        $"单文件上限约 {ActivityLog.DefaultMaxFileBytes / 1024 / 1024} MB，最多 {ActivityLog.DefaultMaxFiles} 个归档；" +
        "每条事件包含时间、级别、类别、来源与内容。";

    protected override Task LoadAsync()
    {
        RefreshFilter();
        return Task.CompletedTask;
    }

    private bool FilterItem(object item)
    {
        if (item is not LogItem log) return false;

        if (_selectedCategory is not null && _selectedCategory.Value != "all" &&
            !string.Equals(log.Category.ToString(), _selectedCategory.Value, StringComparison.Ordinal))
            return false;

        if (_selectedLevel is not null && _selectedLevel.Value != "all" &&
            !string.Equals(log.Level.ToString(), _selectedLevel.Value, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var needle = _searchText.Trim();
        return log.Message.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               log.Source.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               log.CategoryText.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               (log.Detail?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RefreshFilter()
    {
        FilteredItems.Clear();
        foreach (var item in _source.Items)
        {
            if (FilterItem(item)) FilteredItems.Add(item);
        }
        FilteredCount = FilteredItems.Count;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems is { Count: 1 } added &&
            added[0] is LogItem item &&
            FilterItem(item))
        {
            FilteredItems.Insert(0, item);
            FilteredCount = FilteredItems.Count;
            OnPropertyChanged(nameof(CountText));
            return;
        }

        RefreshFilter();
    }

    private void ClearLog()
    {
        var result = MessageBox.Show(
            "将删除本机保存的全部运行/通信日志文件（包括轮转归档）。\n\n" +
            "设备状态、电池历史和其他设置不会受影响。确定继续？",
            "清空持久化日志", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var ok = _source.ClearPersistent();
        RefreshFilter();
        OnPropertyChanged(nameof(StorageError));
        OnPropertyChanged(nameof(HasStorageError));
        ShowMessage(ok ? "本地日志文件已清空，并已写入新的日志起点。" : "日志文件未能全部删除，错误已显示在页面顶部。",
            isError: !ok);
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_source.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = _source.DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowMessage("无法打开日志目录：" + ex.Message, isError: true);
        }
    }

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ActivityLog.LastWriteError) or nameof(ActivityLog.HasWriteError)
            or nameof(ActivityLog.DroppedCount))) return;

        void Raise()
        {
            OnPropertyChanged(nameof(StorageError));
            OnPropertyChanged(nameof(HasStorageError));
            OnPropertyChanged(nameof(DroppedCount));
            OnPropertyChanged(nameof(CountText));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Raise();
        else dispatcher.BeginInvoke(new Action(Raise));
    }
}
