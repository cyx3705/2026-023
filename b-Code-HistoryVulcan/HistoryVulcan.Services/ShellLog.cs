using System.Collections.Concurrent;
using System.Text;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services;

/// <summary>
/// 正式日志服务(§6.2,M2):
/// - L-01 多输出端:内存缓冲(控制台窗口补读)/ 滚动文件 / IDE 调试输出
/// - L-02 滚动:按天切分 + 单文件超 10MB 续号切分;保留天数可配(默认 30 天)
/// - L-03 指令回显与普通日志共用本管道、以类别区分(cmd:* 前缀)
/// - N-03 承压:文件写入走后台单线程 + 常开流,1000 条/秒不阻塞调用方
/// 文件始终全量落盘;控制台显示级别过滤在窗口侧完成(L-04)。
/// </summary>
public sealed class ShellLog : IShellLog, IDisposable
{
    private const int BufferLimit = 50_000;          // C-05 内存缓冲上限
    private const long RollSizeBytes = 10 * 1024 * 1024; // L-02 尺寸滚动阈值

    private readonly object _gate = new();
    private readonly Queue<ShellLogEntry> _buffer = new();
    private readonly BlockingCollection<ShellLogEntry> _fileQueue = new(boundedCapacity: 200_000);
    private readonly Thread _writerThread;
    private readonly string _logsDir;
    private readonly int _retainDays;

    private StreamWriter? _writer;
    private string _writerDate = "";
    private int _writerSeq;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ShellLog(AppPaths paths, int retainDays = 30)
    {
        _logsDir = paths.LogsDir;
        _retainDays = Math.Max(1, retainDays);

        CleanupOldFiles();

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "ShellLog.Writer",
        };
        _writerThread.Start();
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public event EventHandler<ShellLogEntry>? EntryAdded;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.Now, level, category, message);

        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > BufferLimit)
                _buffer.Dequeue();
        }

        // 文件队列满(极端涌入)时丢弃文件侧,内存与事件端不受影响
        _fileQueue.TryAdd(entry);

        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Trace.WriteLine($"[{entry.Level}] [{entry.Category}] {entry.Message}");

        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlyList<ShellLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToList();
        }
    }

    /// <summary>退出前冲刷文件队列(App 关闭时调用)。</summary>
    public void Dispose()
    {
        _fileQueue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(3));
        _writer?.Dispose();
    }

    // ---------------------------------------------------------------- 后台文件写入

    private void WriterLoop()
    {
        try
        {
            foreach (var entry in _fileQueue.GetConsumingEnumerable())
            {
                WriteEntry(entry);

                // 队列空了才冲刷,涌入期间合并写盘
                if (_fileQueue.Count == 0)
                    _writer?.Flush();
            }
        }
        catch (Exception)
        {
            // 写线程崩溃不影响程序(N-05);内存端仍工作
        }
        finally
        {
            try
            {
                _writer?.Flush();
            }
            catch (IOException)
            {
            }
        }
    }

    private void WriteEntry(ShellLogEntry entry)
    {
        try
        {
            EnsureWriter(entry.Time);
            _writer!.WriteLine(
                $"{entry.Time:HH:mm:ss.fff} [{entry.Level}] [{entry.Category}] {entry.Message}");
        }
        catch (IOException)
        {
            // 单条写失败忽略;下一条重试建流
            try { _writer?.Dispose(); }
            catch (IOException) { }
            _writer = null;
        }
    }

    private void EnsureWriter(DateTime time)
    {
        var date = time.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (_writer != null && date == _writerDate)
        {
            if (_writer.BaseStream.Length < RollSizeBytes)
                return;
            _writerSeq++; // 超 10MB 续号切分
        }
        else if (date != _writerDate)
        {
            _writerDate = date;
            _writerSeq = 0;
        }

        _writer?.Dispose();

        // 找到当天第一个未超限的续号(重启后接着写)
        while (true)
        {
            var path = FilePath(date, _writerSeq);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < RollSizeBytes)
            {
                _writer = new StreamWriter(path, append: true, Encoding.UTF8);
                return;
            }

            _writerSeq++;
        }
    }

    private string FilePath(string date, int seq)
        => Path.Combine(_logsDir, seq == 0 ? $"shell-{date}.log" : $"shell-{date}.{seq}.log");

    private void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retainDays);
            foreach (var file in Directory.EnumerateFiles(_logsDir, "shell-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }
}
