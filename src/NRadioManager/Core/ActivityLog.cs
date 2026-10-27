using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Windows.Threading;
using NRadioManager.Infrastructure;

namespace NRadioManager.Core;

public enum LogLevel { Info, Success, Warning, Error }

/// <summary>运行事件的业务类别，供通信日志页面筛选。</summary>
public enum LogCategory
{
    Notification,
    Connection,
    Device,
    Communication,
    Sampling,
    Network,
    Application,
    Configuration,
    Data,
    Persistence,
    Other,
}

/// <summary>
/// 一条持久化运行事件。Time 是本机时间；Detail 仅用于异常堆栈等补充诊断信息。
/// </summary>
public sealed record LogItem(
    DateTime Time,
    LogLevel Level,
    LogCategory Category,
    string Source,
    string Message,
    string? Detail = null)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string DateText => Time.ToString("yyyy-MM-dd HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Success => "成功",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    public string CategoryText => Category switch
    {
        LogCategory.Notification => "通知",
        LogCategory.Connection => "连接",
        LogCategory.Device => "设备",
        LogCategory.Communication => "通信",
        LogCategory.Sampling => "采样",
        LogCategory.Network => "网络",
        LogCategory.Application => "运行",
        LogCategory.Configuration => "配置",
        LogCategory.Data => "数据",
        LogCategory.Persistence => "存储",
        _ => "其他",
    };

    public string SourceText => string.IsNullOrWhiteSpace(Source) ? "—" : Source;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// 软件统一运行事件日志。
///
/// 内存集合与磁盘文件使用同一个 LogItem 数据源：每条事件只发布一次，先进入有界写入队列，
/// 再由唯一的刷新任务按顺序追加到 %APPDATA%\NRadioManager\activity.log。
/// 文件按大小轮转（activity.1.log … activity.N.log），并按保留天数清理旧归档。
/// 记录频率由各事件源控制，轮询成功等高频事件不会逐条落盘。
/// </summary>
public sealed class ActivityLog : ObservableObject, IDisposable
{
    public const int DefaultMaxMemoryItems = 5000;
    public const long DefaultMaxFileBytes = 2 * 1024 * 1024;
    public const int DefaultMaxFiles = 3;
    public const int DefaultRetentionDays = 30;

    private const int MaxPendingItems = 2000;
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly Regex SecretAssignment = new(
        @"(?i)(\b(?:authorization|proxy-authorization)\b\s*[:=]\s*)[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex SecretValue = new(
        @"(?i)(""?\b(?:password|passwd|pwd|token|access_token|refresh_token|key|secret|"
        + @"cnonce|nonce|response|cookie|sessionid)\b""?\s*[:=]\s*)"
        + @"(""(?:[^""\\]|\\.)*""|[^\s,;&}\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex SecretQuery = new(
        @"(?i)([?&](?:password|passwd|pwd|token|key|response|cnonce|nonce)=)[^&\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex SmsOrIdentityField = new(
        @"(?i)(""?\b(?:phone_num|phone|recipient|sms_body|sms_msg|imsi|iccid|imei)\b""?\s*[:=]\s*)"
        + @"(""(?:[^""\\]|\\.)*""|[^\s,;&}\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ChineseMobileNumber = new(
        @"(?<!\d)(?:\+?86[- ]?)?1[3-9]\d{9}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly object _queueGate = new();
    private readonly object _fileGate = new();
    private readonly object _memoryGate = new();
    private readonly object _addGate = new();
    private readonly Queue<LogItem> _pending = new();
    private readonly int _maxMemoryItems;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly int _retentionDays;
    private readonly bool _publishToUi;
    private Task? _flushTask;
    private bool _disposed;
    private long _persistedCount;

    public ActivityLog(
        string? directory = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFiles = DefaultMaxFiles,
        int retentionDays = DefaultRetentionDays,
        int maxMemoryItems = DefaultMaxMemoryItems,
        bool loadExisting = true,
        bool publishToUi = true)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(directory) ? AppSettings.DataDir : directory;
        CurrentFilePath = Path.Combine(DirectoryPath, "activity.log");
        _maxFileBytes = Math.Max(16 * 1024, maxFileBytes);
        _maxFiles = Math.Clamp(maxFiles, 2, 10);
        _retentionDays = Math.Clamp(retentionDays, 1, 3650);
        _maxMemoryItems = Math.Clamp(maxMemoryItems, 100, 100_000);
        _publishToUi = publishToUi;

        Items = new ObservableCollection<LogItem>();
        if (!loadExisting) return;

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            CleanupExpiredFiles();
            LoadExistingFiles();
        }
        catch (Exception ex)
        {
            LastWriteError = $"日志目录或历史文件不可用：{ex.GetType().Name}: {ex.Message}";
            LoadWarning = "历史日志读取失败；本次运行的新日志仍会尝试写入。";
        }
    }

    public ObservableCollection<LogItem> Items { get; }
    public string DirectoryPath { get; }
    public string CurrentFilePath { get; }
    public int LoadedCount { get; private set; }
    public int DroppedCount { get; private set; }
    /// <summary>本进程成功写入磁盘的事件数，用于诊断与测试。</summary>
    public long PersistedCount => Interlocked.Read(ref _persistedCount);
    public string? LoadWarning { get; private set; }
    public string? LastWriteError { get; private set; }
    public bool HasWriteError => !string.IsNullOrWhiteSpace(LastWriteError);

    /// <summary>用于诊断展示；事件处理器不得在此回调里再次写日志，避免故障递归。</summary>
    public event Action<string>? WriteFailed;

    public void Add(LogLevel level, string message)
        => Add(level, LogCategory.Other, "应用", message);

    public void Info(string message) => Add(LogLevel.Info, LogCategory.Other, "应用", message);
    public void Success(string message) => Add(LogLevel.Success, LogCategory.Other, "应用", message);
    public void Warn(string message) => Add(LogLevel.Warning, LogCategory.Other, "应用", message);
    public void Error(string message) => Add(LogLevel.Error, LogCategory.Other, "应用", message);

    public void Add(LogLevel level, LogCategory category, string source, string message, string? detail = null)
    {
        if (_disposed) return;

        var safeMessage = Sanitize(message);
        var safeSource = Sanitize(source);
        var safeDetail = string.IsNullOrWhiteSpace(detail) ? null : Sanitize(detail);
        var item = new LogItem(DateTime.Now, level, category, safeSource, safeMessage, safeDetail);

        lock (_addGate)
        {
            Publish(item);
            Enqueue(item);
        }
    }

    public void Info(LogCategory category, string source, string message, string? detail = null)
        => Add(LogLevel.Info, category, source, message, detail);

    public void Success(LogCategory category, string source, string message, string? detail = null)
        => Add(LogLevel.Success, category, source, message, detail);

    public void Warn(LogCategory category, string source, string message, string? detail = null)
        => Add(LogLevel.Warning, category, source, message, detail);

    public void Error(LogCategory category, string source, string message, string? detail = null)
        => Add(LogLevel.Error, category, source, message, detail);

    /// <summary>
    /// 等待待写事件落盘。退出流程必须调用；超时返回 false，调用方可提示用户，
    /// 但不会在主线程上无限等待。
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var failedAttempts = 0;

        while (true)
        {
            Task? task;
            lock (_queueGate)
            {
                if (_pending.Count == 0 && _flushTask is null) return true;
                task = _flushTask ??= Task.Run(FlushLoop);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            if (!task.Wait(remaining)) return false;

            lock (_queueGate)
            {
                if (_pending.Count == 0) return true;
            }

            if (HasWriteError)
            {
                failedAttempts++;
                if (failedAttempts >= 2 || DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(WriteRetryDelay);
            }
        }
    }

    /// <summary>在用户明确确认后删除全部日志文件，并写入一条新的“日志已清空”事件。</summary>
    public bool ClearPersistent()
    {
        lock (_queueGate) _pending.Clear();

        Task? activeFlush;
        lock (_queueGate) activeFlush = _flushTask;
        try { activeFlush?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 清空操作仍应继续 */ }

        var ok = true;
        lock (_fileGate)
        {
            foreach (var path in LogFilePaths().Append(CurrentFilePath))
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    ok = false;
                    LastWriteError = $"删除日志文件失败：{ex.GetType().Name}: {ex.Message}";
                }
            }
        }

        lock (_memoryGate) Items.Clear();
        Info(LogCategory.Application, "通信日志", "用户已清空历史日志文件；从这里开始记录新的运行事件。");
        Flush(TimeSpan.FromSeconds(2));
        RaiseStatusChanged();
        return ok;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush(TimeSpan.FromSeconds(3));
        _disposed = true;
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            var safe = SecretAssignment.Replace(text, "$1***");
            safe = SecretValue.Replace(safe, "$1***");
            safe = SecretQuery.Replace(safe, "$1***");
            safe = SmsOrIdentityField.Replace(safe, "$1***");
            safe = ChineseMobileNumber.Replace(safe, "[手机号已脱敏]");
            return safe;
        }
        catch (RegexMatchTimeoutException)
        {
            // 极端长文本下宁可少记录，也不能把可能的凭据原样写盘。
            return "[文本过长或脱敏超时，已省略]";
        }
    }

    private void Publish(LogItem item)
    {
        if (!_publishToUi) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            PublishOnUi(item);
            return;
        }

        // 后台请求不得阻塞等待 UI 线程。
        dispatcher.BeginInvoke(new Action(() => PublishOnUi(item)), DispatcherPriority.Background);
    }

    private void PublishOnUi(LogItem item)
    {
        lock (_memoryGate)
        {
            Items.Insert(0, item);
            while (Items.Count > _maxMemoryItems)
                Items.RemoveAt(Items.Count - 1);
        }
    }

    private void Enqueue(LogItem item)
    {
        lock (_queueGate)
        {
            _pending.Enqueue(item);
            while (_pending.Count > MaxPendingItems)
            {
                _pending.Dequeue();
                DroppedCount++;
            }

            _flushTask ??= Task.Run(FlushLoop);
        }
    }

    private void FlushLoop()
    {
        while (true)
        {
            LogItem[] batch;
            lock (_queueGate)
            {
                if (_pending.Count == 0)
                {
                    _flushTask = null;
                    return;
                }

                batch = _pending.ToArray();
                _pending.Clear();
            }

            try
            {
                WriteBatch(batch);
                Interlocked.Add(ref _persistedCount, batch.Length);
                var hadError = HasWriteError;
                LastWriteError = null;
                if (hadError) RaiseStatusChanged();
            }
            catch (Exception ex)
            {
                PutBack(batch);
                RecordWriteFailure(ex);
                lock (_queueGate) _flushTask = null;
                return;
            }
        }
    }

    private void PutBack(LogItem[] batch)
    {
        lock (_queueGate)
        {
            var existing = _pending.ToArray();
            _pending.Clear();

            var start = Math.Max(0, batch.Length + existing.Length - MaxPendingItems);
            for (var i = start; i < batch.Length; i++) _pending.Enqueue(batch[i]);
            for (var i = 0; i < existing.Length; i++) _pending.Enqueue(existing[i]);
            if (start > 0) DroppedCount += start;
        }
    }

    private void WriteBatch(LogItem[] batch)
    {
        if (batch.Length == 0) return;
        var chunks = new List<(string Text, int Bytes)>();
        var current = new StringBuilder();
        var currentBytes = 0;
        foreach (var item in batch)
        {
            var line = SerializeLine(item);
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (currentBytes > 0 && currentBytes + lineBytes > _maxFileBytes)
            {
                chunks.Add((current.ToString(), currentBytes));
                current.Clear();
                currentBytes = 0;
            }
            current.Append(line);
            currentBytes += lineBytes;
        }
        if (currentBytes > 0) chunks.Add((current.ToString(), currentBytes));

        foreach (var chunk in chunks)
            WriteChunk(chunk.Text, chunk.Bytes);
    }

    private void WriteChunk(string text, int bytes)
    {
        lock (_fileGate)
        {
            // 用户在运行期间删除目录、或程序首次写日志时目录尚未创建，都在写入前兜底创建。
            Directory.CreateDirectory(DirectoryPath);
            RotateIfNeeded(bytes);
            var info = new FileInfo(CurrentFilePath);
            var needsLeadingNewline = info.Exists && info.Length > 0 && !EndsWithNewline(CurrentFilePath);

            using var stream = new FileStream(
                CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            if (needsLeadingNewline)
            {
                stream.WriteByte((byte)'\n');
            }

            var payload = Encoding.UTF8.GetBytes(text);
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
        }
    }

    private static string SerializeLine(LogItem item)
        => JsonSerializer.Serialize(new PersistedLogItem
        {
            Time = item.Time,
            Level = item.Level,
            Category = item.Category,
            Source = item.Source,
            Message = item.Message,
            Detail = item.Detail,
        }, JsonOptions) + "\n";

    private void RotateIfNeeded(long incomingBytes)
    {
        var current = new FileInfo(CurrentFilePath);
        if (!current.Exists || current.Length == 0 || current.Length + incomingBytes <= _maxFileBytes)
            return;

        var oldest = LogFilePaths().Last();
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var index = _maxFiles - 1; index >= 1; index--)
        {
            var source = ArchivePath(index);
            if (!File.Exists(source)) continue;
            File.Move(source, ArchivePath(index + 1), overwrite: true);
        }

        File.Move(CurrentFilePath, ArchivePath(1), overwrite: true);
    }

    private void CleanupExpiredFiles()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var path in LogFilePaths().Append(CurrentFilePath))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.LastWriteTimeUtc >= cutoff) continue;
                info.Delete();
            }
            catch
            {
                // 单个旧文件被占用不应阻止启动。
            }
        }
    }

    private void LoadExistingFiles()
    {
        var invalidLines = 0;
        var loaded = new List<LogItem>();
        var files = LogFilePaths().Reverse().Append(CurrentFilePath).ToList();

        foreach (var path in files)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var record = JsonSerializer.Deserialize<PersistedLogItem>(line, JsonOptions);
                        if (record is null || string.IsNullOrWhiteSpace(record.Message)) continue;
                        loaded.Add(new LogItem(
                            record.Time,
                            record.Level,
                            record.Category,
                            record.Source ?? "应用",
                            Sanitize(record.Message),
                            string.IsNullOrWhiteSpace(record.Detail) ? null : Sanitize(record.Detail)));
                    }
                    catch (JsonException)
                    {
                        invalidLines++;
                    }
                }
            }
            catch (Exception ex)
            {
                invalidLines++;
                LoadWarning = $"无法读取日志文件 {Path.GetFileName(path)}：{ex.Message}";
            }
        }

        loaded.Sort((a, b) => a.Time.CompareTo(b.Time));
        var start = Math.Max(0, loaded.Count - _maxMemoryItems);
        LoadedCount = loaded.Count;
        for (var i = loaded.Count - 1; i >= start; i--)
            Items.Add(loaded[i]);

        if (invalidLines > 0)
        {
            var corrupt = $"读取日志时跳过 {invalidLines} 行损坏内容；其余记录已正常加载。";
            LoadWarning = string.IsNullOrWhiteSpace(LoadWarning) ? corrupt : LoadWarning + " " + corrupt;
        }
    }

    private IEnumerable<string> LogFilePaths()
        => Enumerable.Range(1, _maxFiles).Select(ArchivePath);

    private string ArchivePath(int index)
        => Path.Combine(DirectoryPath, $"activity.{index}.log");

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() is (byte)'\n' or (byte)'\r';
    }

    private void RecordWriteFailure(Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        var changed = !string.Equals(LastWriteError, message, StringComparison.Ordinal);
        LastWriteError = message;

        if (!changed) return;
        RaiseStatusChanged();

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(
                Path.Combine(DirectoryPath, "activity-write-error.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 日志写入失败，事件已保留在内存队列等待重试：{message}\r\n");
        }
        catch
        {
            // 错误报告自身失败时只能保留在 LastWriteError，绝不递归写日志。
        }

        try { WriteFailed?.Invoke(message); }
        catch { /* 订阅者的异常不能影响日志队列 */ }
    }

    private void RaiseStatusChanged()
    {
        OnPropertyChanged(nameof(LastWriteError));
        OnPropertyChanged(nameof(HasWriteError));
        OnPropertyChanged(nameof(DroppedCount));
    }

    private sealed class PersistedLogItem
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public LogCategory Category { get; set; }
        public string? Source { get; set; }
        public string? Message { get; set; }
        public string? Detail { get; set; }
    }
}
