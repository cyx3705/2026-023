using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.CommandSurface;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Shell.Console;

/// <summary>
/// 控制台窗口(§4.4):程序的“素颜”。
/// 输出区 = 指令回显 + 执行结果 + 运行日志(共用 IShellLog 管道);
/// 输入区 = 手动指令入口。仅靠本窗口即可驱动整个程序(架构不变量 2)。
/// 承压设计(N-03):日志事件先进并发队列,UI 以 100ms 批量合并刷新;
/// 列表虚拟化 + 环形缓冲上限(C-05)。
/// </summary>
public partial class ConsoleView : UserControl, Core.Modules.IActivatableToolContent
{
    public const string KeyHistory = "console.history";
    public const string KeyBuffer = "console.buffer";

    private readonly IShellLog _log;
    private readonly CommandBus _bus;
    private readonly CommandHistory _history;
    private readonly ICommandCatalogSession _catalogSession;
    private readonly int _bufferLimit;

    private readonly ConcurrentQueue<ShellLogEntry> _incoming = new();
    private int _incomingCount;
    private readonly RingCollection<ConsoleRow> _all = new();
    private RingCollection<ConsoleRow> _visible = new();
    private readonly DispatcherTimer _flushTimer;
    private ScrollViewer? _scroll;
    private string _domain = "全部";
    private string _commandClass = "全部";
    private IReadOnlyList<string> _domains = ["全部"];
    private IReadOnlyList<string> _classes = ["全部"];
    private IReadOnlySet<string> _registeredDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "core",
    };
    private string _keyword = "";
    private bool _muteLayout;
    private bool _autoScroll = true;
    private bool _suppressFilterEvents;

    // 输入区状态
    private int _historyIndex = -1;
    private string _draft = "";
    private ConsoleCompletionResult _completionResult = ConsoleCompletionResult.Empty;
    private int _completionIndex;
    private bool _suppressTextChanged;
    private Func<bool>? _completionFocusPredicate;
    private Action? _showCommandCatalog;
    private bool _completionFocusEnabled = true;
    private bool _catalogShownForInput;
    private CancellationTokenSource? _completionRefresh;

    public ConsoleView(
        IShellLog log,
        CommandBus bus,
        CommandHistory history,
        ICommandCatalogSession catalogSession,
        int bufferLimit = 50_000)
    {
        InitializeComponent();

        _log = log;
        _bus = bus;
        _history = history;
        _catalogSession = catalogSession;
        _bufferLimit = Math.Max(1000, bufferLimit);

        LevelFilter.ItemsSource = new[] { "全部", "Trace", "Debug", "Info", "Warn", "Error", "Fatal" };
        LevelFilter.SelectedIndex = 0;
        DomainFilter.ItemsSource = new[] { "全部" };
        DomainFilter.SelectedIndex = 0;
        ClassFilter.ItemsSource = new[] { "全部" };
        ClassFilter.SelectedIndex = 0;
        ClassFilter.IsEnabled = false;

        CompletionList.ItemsSource = Array.Empty<ConsoleCompletionCandidate>();
        CompletionPopup.IsOpen = false;
        Output.ItemsSource = _visible;

        // 已有历史补读 + 增量订阅
        foreach (var e in log.Snapshot())
            EnqueueIncoming(e);
        log.EntryAdded += (_, e) =>
        {
            if (e.Category.Equals(CommandBus.EchoCategoryPrefix + "手动", StringComparison.OrdinalIgnoreCase))
                _history.Add(e.Message);
            EnqueueIncoming(e);
        };

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => FlushIncoming();
        _flushTimer.Start();

        // C-14:粘贴多行文本 → 按行拆分为多条指令顺序执行
        DataObject.AddPastingHandler(Input, OnInputPasting);

        Loaded += async (_, _) =>
        {
            _scroll ??= FindScrollViewer(Output);
            _catalogSession.Changed -= OnCatalogChanged;
            _catalogSession.Changed += OnCatalogChanged;
            await RefreshDomainsAsync();
        };
        Unloaded += (_, _) =>
        {
            HideCompletions();
            _catalogSession.Changed -= OnCatalogChanged;
        };
    }

    /// <summary>当前控制台显示级别(vulcan.log.level,L-04;文件始终全量)。</summary>
    public ShellLogLevel MinLevel { get; private set; } = ShellLogLevel.Trace;

    /// <summary>vulcan.log.level 指令入口:调整显示级别过滤。</summary>
    public void SetMinLevel(ShellLogLevel level)
    {
        MinLevel = level;
        _suppressFilterEvents = true;
        try
        {
            LevelFilter.SelectedIndex = level == ShellLogLevel.Trace ? 0 : (int)level + 1;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
        RebuildVisible();
    }

    internal string SourceFilterValue => _domain;
    internal string ClassFilterValue => _commandClass;
    internal string KeywordFilterValue => _keyword;
    internal bool MuteLayoutEnabled => _muteLayout;
    internal bool AutoScrollEnabled => _autoScroll;

    /// <inheritdoc />
    public void ActivateContent() => FocusInput();

    /// <summary>由 ShellWindow 注入聚焦判定和非聚焦命令集切换；保持为内部接线，不进入公开 API。</summary>
    internal void ConfigureCompletionRouting(
        Func<bool> completionFocusPredicate,
        Action showCommandCatalog)
    {
        _completionFocusPredicate = completionFocusPredicate;
        _showCommandCatalog = showCommandCatalog;
        RefreshCompletionFocus();
    }

    /// <summary>停靠布局改变后刷新候选模式；非聚焦时不显示候选 Popup。</summary>
    internal void RefreshCompletionFocus()
    {
        var enabled = _completionFocusPredicate?.Invoke() ?? true;
        if (enabled != _completionFocusEnabled)
        {
            _completionFocusEnabled = enabled;
            _catalogShownForInput = false;
        }

        if (!enabled)
        {
            HideCompletions();
            SyncCommandCatalog(showCatalog: true);
        }
        else if (Input.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(Input.Text))
            RefreshCompletions();
        else
            _catalogSession.SetConsoleQuery("");
    }

    internal bool TrySetSource(string source, out IReadOnlyList<string> availableDomains)
    {
        FlushIncoming();
        if (!_catalogSession.TrySetDomain(source, out availableDomains))
            return false;
        ApplyTaxonomySnapshot();
        RebuildVisible();
        return true;
    }

    internal void SetSource(string source)
        => _ = TrySetSource(source, out _);

    internal bool TrySetClass(string commandClass, out IReadOnlyList<string> availableClasses)
    {
        FlushIncoming();
        if (!_catalogSession.TrySetCommandClass(commandClass, out availableClasses))
            return false;
        ApplyTaxonomySnapshot();
        RebuildVisible();
        return true;
    }

    internal void SetKeyword(string keyword)
    {
        _keyword = keyword ?? "";
        _suppressFilterEvents = true;
        try { KeywordFilter.Text = _keyword; }
        finally { _suppressFilterEvents = false; }
        RebuildVisible();
    }

    internal void SetMuteLayout(bool enabled)
    {
        _muteLayout = enabled;
        _suppressFilterEvents = true;
        try { MuteLayout.IsChecked = enabled; }
        finally { _suppressFilterEvents = false; }
        RebuildVisible();
    }

    internal void SetAutoScroll(bool enabled)
    {
        _autoScroll = enabled;
        _suppressFilterEvents = true;
        try { AutoScroll.IsChecked = enabled; }
        finally { _suppressFilterEvents = false; }
    }

    /// <summary>cls 指令入口:清空显示(不清日志文件,C-06)。</summary>
    public void Cls()
    {
        _all.Clear();
        _visible = new RingCollection<ConsoleRow>();
        Output.ItemsSource = _visible;
    }

    internal void AddTransientEntry(ShellLogEntry entry) => EnqueueIncoming(entry);

    internal string CopySelected()
    {
        if (Output.SelectedItems.Count == 0)
            return "没有选中的控制台行";

        var selected = new HashSet<object>(Output.SelectedItems.Cast<object>());
        var text = string.Join(Environment.NewLine, _visible.Where(selected.Contains).Select(row => row.Text));
        try
        {
            Clipboard.SetText(text);
            return $"已复制 {selected.Count} 行";
        }
        catch (Exception ex)
        {
            _log.Error("console", $"复制失败: {ex.Message}");
            return "复制失败";
        }
    }

    /// <summary>
    /// 导出控制台可见内容。REQ-CMD-012:成功路径不得依赖人工点击——省略 <paramref name="path"/>
    /// 时写入数据目录下带时间戳的默认文件并回报绝对路径,而不是弹 <c>SaveFileDialog</c>。
    /// 否则 <c>vulcan.log.export</c> 经 MCP / Web / 前端转发调用会阻塞 UI 线程等待一个不会到来的点击。
    /// </summary>
    internal string ExportVisible(string? path)
    {
        var resolved = path?.Trim();
        try
        {
            resolved = string.IsNullOrWhiteSpace(resolved)
                ? BuildDefaultExportPath()
                : Path.GetFullPath(resolved);

            var directory = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(resolved, _visible.ToList().Select(row => row.Text));
            _log.Info("console", $"已导出 {_visible.Count} 行到 {resolved}");
            return $"已导出 {_visible.Count} 行到 {resolved}";
        }
        catch (Exception ex)
        {
            _log.Error("console", $"导出失败: {ex.Message}");
            return $"导出失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 默认导出落点:<c>%AppData%/&lt;应用名&gt;/exports/console-&lt;时间戳&gt;.txt</c>,
    /// 与 <c>AppPaths</c> 的数据根目录约定一致。
    /// </summary>
    private static string BuildDefaultExportPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.Current.Name,
            "exports");
        return Path.Combine(
            Directory.CreateDirectory(root).FullName,
            $"console-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
    }

    /// <summary>聚焦输入框(C-15 全局快捷键落点)。</summary>
    public void FocusInput()
    {
        // AvalonDock may finish showing the anchorable after this method returns.
        // Queue the focus operation so the real TextBox, rather than its host, receives keys.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible)
                return;

            Input.Focusable = true;
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;
            RefreshCompletions();
        }));
    }

    /// <summary>把仅 Error 级别可见设为当前过滤(状态栏错误计数跳转用,S-03)。</summary>
    public void FilterErrorsOnly()
    {
        SetMinLevel(ShellLogLevel.Error);
        SetSource("全部");
        SetKeyword("");
        SetMuteLayout(false);
    }

    /// <summary>错误自动跳转使用：清除过滤，保证输入回显与错误结果同时可见。</summary>
    internal void ResetFilters()
    {
        SetMinLevel(ShellLogLevel.Trace);
        SetSource("全部");
        SetKeyword("");
        SetMuteLayout(false);
    }

    // ---------------------------------------------------------------- 输出区

    private void EnqueueIncoming(ShellLogEntry entry)
    {
        _incoming.Enqueue(entry);
        var count = Interlocked.Increment(ref _incomingCount);
        while (count > _bufferLimit && _incoming.TryDequeue(out _))
            count = Interlocked.Decrement(ref _incomingCount);
    }

    private void FlushIncoming()
    {
        if (_incoming.IsEmpty)
            return;

        var appended = false;
        // 单次最多处理 8000 条,极端涌入时分帧消化,保 UI 响应(N-03)
        for (var i = 0; i < 8000 && _incoming.TryDequeue(out var entry); i++)
        {
            Interlocked.Decrement(ref _incomingCount);
            foreach (var row in ConsoleRow.From(entry, _bus.Registry))
            {
                _all.Add(row);
                if (PassesFilter(row))
                {
                    _visible.Add(row);
                    appended = true;
                }
            }
        }

        _all.TrimTo(_bufferLimit);
        _visible.TrimTo(_bufferLimit);

        if (appended && _autoScroll)
            _scroll?.ScrollToBottom();
    }

    private bool PassesFilter(ConsoleRow row)
    {
        if (row.Level < MinLevel)
            return false;

        if (_muteLayout && row.SourceKey == "layout")
            return false;

        if (_domain != "全部" && !EffectiveDomainOf(row).Equals(_domain, StringComparison.OrdinalIgnoreCase))
            return false;
        // DEC-025：筛选项存的是显示标签，「无类」要先翻回空串类键再比较。
        if (_commandClass != "全部" && !row.CommandClassKey.Equals(
                CommandClassLabels.ToKey(_commandClass),
                StringComparison.OrdinalIgnoreCase))
            return false;

        var keyword = _keyword;
        if (keyword.Length > 0
            && !row.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void RebuildVisible()
    {
        // 重建可见列表(换新集合整体重绑,避免几万条增量通知)
        var next = new RingCollection<ConsoleRow>();
        foreach (var row in _all)
        {
            if (PassesFilter(row))
                next.Add(row);
        }

        _visible = next;
        Output.ItemsSource = _visible;
        if (_autoScroll)
            _scroll?.ScrollToBottom();
    }

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents || LevelFilter.SelectedIndex < 0)
            return;
        var level = LevelFilter.SelectedIndex == 0 ? "trace" : ((ShellLogLevel)(LevelFilter.SelectedIndex - 1)).ToString().ToLowerInvariant();
        _ = _bus.ExecuteAsync($"vulcan.log.level level={level}", "UI");
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents || DomainFilter.SelectedItem is not string source)
            return;
        _ = _bus.ExecuteAsync($"vulcan.log.source source={CommandParser.QuoteArg(source)}", "UI");
    }

    private void OnClassChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents || ClassFilter.SelectedItem is not string commandClass)
            return;
        _ = _bus.ExecuteAsync($"vulcan.log.class class={CommandParser.QuoteArg(commandClass)}", "UI");
    }

    private void OnCatalogChanged(object? sender, CommandCatalogChangedEventArgs e)
    {
        if (e.Kind == CommandCatalogChangeKind.Selection)
            return;
        Dispatcher.BeginInvoke(async () =>
        {
            if (!IsLoaded)
                return;
            if (e.Kind == CommandCatalogChangeKind.Invalidated)
                await RefreshDomainsAsync();
            else
                ApplyTaxonomySnapshot();
            RefreshCompletions();
        });
    }

    private async Task RefreshDomainsAsync()
    {
        if (!await _catalogSession.RefreshAsync())
            return;

        ApplyTaxonomySnapshot();
    }

    private void ApplyTaxonomySnapshot()
    {
        var registered = _catalogSession.Domains
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        _registeredDomains = registered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _domains = ["全部", .. registered];
        var filter = _catalogSession.CurrentFilter;
        var previousDomain = _domain;
        var previousClass = _commandClass;
        _domain = filter.Domain;
        _classes = ["全部", .. _catalogSession.Classes];
        _commandClass = filter.CommandClass;
        _suppressFilterEvents = true;
        try
        {
            DomainFilter.ItemsSource = _domains;
            DomainFilter.SelectedItem = _domain;
            ClassFilter.ItemsSource = _classes;
            ClassFilter.SelectedItem = _commandClass;
            ClassFilter.IsEnabled = _domain != "全部";
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        if (!previousDomain.Equals(_domain, StringComparison.Ordinal)
            || !previousClass.Equals(_commandClass, StringComparison.Ordinal))
            RebuildVisible();
    }

    private string EffectiveDomainOf(ConsoleRow row)
        => _registeredDomains.Contains(row.DomainKey) ? row.DomainKey : "core";

    private void OnClearClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.log.clear", "UI");

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.log.copy", "UI");

    private void OnExportClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.log.export", "UI");

    // ---------------------------------------------------------------- 输入区

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleCompletionKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Submit(Input.Text);
                e.Handled = true;
                break;

            case Key.Up:
                NavigateHistory(-1);
                e.Handled = true;
                break;

            case Key.Down:
                NavigateHistory(+1);
                e.Handled = true;
                break;

            case Key.Escape:
                SetInputText("");
                _historyIndex = -1;
                HideCompletions();
                e.Handled = true;
                break;
        }
    }

    private void Submit(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        // 域聚焦：首段不是已注册域时补上聚焦域前缀（REQ-CMD-012）。
        // 权威源是运行期注册表，不是任何常量域清单。
        text = DomainFocus.Resolve(text, _domain, _bus.Registry.IsRegisteredDomain);

        _historyIndex = -1;
        _draft = "";
        SetInputText("");
        HideCompletions();
        _ = ExecuteAndFlushAsync(text, "手动");
    }

    private async Task ExecuteAndFlushAsync(string text, string source)
    {
        var execution = _bus.ExecuteAsync(text, source);
        FlushIncoming();
        try
        {
            await execution.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("console", $"指令执行异常: {ex.GetType().Name}");
        }

        FlushIncoming();
    }

    private void NavigateHistory(int direction)
    {
        var items = _history.Snapshot();
        if (items.Count == 0)
            return;

        if (_historyIndex == -1)
        {
            if (direction > 0)
                return;
            _draft = Input.Text;
            _historyIndex = items.Count - 1;
        }
        else
        {
            _historyIndex += direction;
        }

        if (_historyIndex >= items.Count)
        {
            _historyIndex = -1;
            SetInputText(_draft);
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex);
        SetInputText(items[_historyIndex]);
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressTextChanged)
            RefreshCompletions(redirectNonFocused: true);
    }

    private void OnInputSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressTextChanged)
            RefreshCompletions();
    }

    private void OnInputLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => HideCompletions();

    private void OnInputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CompletionPopup.IsOpen)
            CompletionBorder.Width = Math.Clamp(Input.ActualWidth, 1, 720);
    }

    private void SetInputText(string text)
    {
        _suppressTextChanged = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressTextChanged = false;
        if (string.IsNullOrWhiteSpace(text))
            _catalogShownForInput = false;
        HideCompletions();
        RefreshCompletions(redirectNonFocused: true);
    }

    private void RefreshCompletions()
        => RefreshCompletions(redirectNonFocused: false);

    private void RefreshCompletions(bool redirectNonFocused)
    {
        _completionRefresh?.Cancel();
        _completionRefresh?.Dispose();
        _completionRefresh = new CancellationTokenSource();
        _ = RefreshCompletionsAsync(redirectNonFocused, _completionRefresh.Token);
    }

    private async Task RefreshCompletionsAsync(
        bool redirectNonFocused,
        CancellationToken cancellationToken)
    {
        if (!IsLoaded)
            return;

        if (string.IsNullOrWhiteSpace(Input.Text))
        {
            _catalogShownForInput = false;
            _catalogSession.SetConsoleQuery("");
            ClearCompletionVisuals();
            return;
        }

        if (!(_completionFocusPredicate?.Invoke() ?? _completionFocusEnabled))
        {
            ClearCompletionVisuals();
            SyncCommandCatalog(redirectNonFocused && Input.IsKeyboardFocusWithin);
            return;
        }

        if (!Input.IsKeyboardFocusWithin)
        {
            ClearCompletionVisuals();
            return;
        }

        _catalogShownForInput = false;
        _catalogSession.SetConsoleQuery("");
        var text = Input.Text;
        var caret = Input.CaretIndex;
        try
        {
            _completionResult = await _catalogSession.CompleteAsync(
                text,
                caret,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested
            || !string.Equals(Input.Text, text, StringComparison.Ordinal)
            || Input.CaretIndex != caret)
        {
            return;
        }
        _completionIndex = 0;
        CompletionList.ItemsSource = _completionResult.Candidates;
        CompletionList.SelectedIndex = _completionResult.HasCandidates ? 0 : -1;
        CompletionBorder.Width = Math.Clamp(Input.ActualWidth, 1, 720);
        CompletionPopup.IsOpen = _completionResult.HasCandidates;
    }

    private void SyncCommandCatalog(bool showCatalog)
    {
        var query = Input.Text;
        _catalogSession.SetConsoleQuery(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            _catalogShownForInput = false;
            return;
        }

        if (!showCatalog || _catalogShownForInput)
            return;

        _catalogShownForInput = true;
        try
        {
            _showCommandCatalog?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("console", $"切换命令集失败: {ex.GetType().Name}");
        }
    }

    private void CycleCompletion(int direction)
    {
        if (!_completionResult.HasCandidates)
            return;

        var count = _completionResult.Candidates.Count;
        _completionIndex = (_completionIndex + direction) % count;
        if (_completionIndex < 0)
            _completionIndex += count;
        CompletionList.SelectedIndex = _completionIndex;
        CompletionList.ScrollIntoView(CompletionList.SelectedItem);
    }

    private void CommitCompletion()
    {
        if (!_completionResult.HasCandidates)
            return;

        var candidate = _completionResult.Candidates[_completionIndex];
        var text = Input.Text.Remove(_completionResult.ReplaceStart, _completionResult.ReplaceLength)
            .Insert(_completionResult.ReplaceStart, candidate.InsertText);
        var caret = _completionResult.ReplaceStart + candidate.InsertText.Length;
        _suppressTextChanged = true;
        Input.Text = text;
        Input.CaretIndex = caret;
        _suppressTextChanged = false;
        if (candidate.Kind is ConsoleCompletionKind.Domain
            or ConsoleCompletionKind.Class
            or ConsoleCompletionKind.Method)
        {
            ClearCompletionVisuals();
            RefreshCompletions();
        }
        else
        {
            HideCompletions();
        }
    }

    private void HideCompletions()
    {
        _completionRefresh?.Cancel();
        _completionRefresh?.Dispose();
        _completionRefresh = null;
        ClearCompletionVisuals();
    }

    private void ClearCompletionVisuals()
    {
        _completionResult = ConsoleCompletionResult.Empty;
        _completionIndex = 0;
        if (CompletionPopup != null)
            CompletionPopup.IsOpen = false;
        if (CompletionList != null)
        {
            CompletionList.ItemsSource = Array.Empty<ConsoleCompletionCandidate>();
            CompletionList.SelectedIndex = -1;
        }
    }

    internal bool HandleCompletionKey(Key key, ModifierKeys modifiers)
    {
        if (_completionResult.HasCandidates && modifiers == ModifierKeys.Shift)
        {
            if (key == Key.W)
            {
                CycleCompletion(-1);
                return true;
            }

            if (key == Key.S)
            {
                CycleCompletion(+1);
                return true;
            }
        }

        var catalogMode = !(_completionFocusPredicate?.Invoke() ?? _completionFocusEnabled)
                          && !string.IsNullOrWhiteSpace(Input.Text);
        if (!_completionResult.HasCandidates && catalogMode && modifiers == ModifierKeys.Shift)
        {
            if (key == Key.W)
                return _catalogSession.MoveSelection(-1);
            if (key == Key.S)
                return _catalogSession.MoveSelection(+1);
        }

        if (key != Key.Tab)
            return false;

        // Shift+Tab is deliberately left to WPF's normal reverse focus traversal.
        if (modifiers != ModifierKeys.None)
            return false;

        if (_completionResult.HasCandidates)
        {
            CommitCompletion();
            return true;
        }

        if (catalogMode && _catalogSession.SelectedCommandName is { } commandName)
        {
            SetInputText(commandName);
            return true;
        }

        // Preserve the existing no-op Tab behavior while the input remains focused.
        return true;
    }

    private void OnInputPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            return;

        var text = (string)e.DataObject.GetData(DataFormats.UnicodeText)!;
        if (!text.Contains('\n'))
            return;

        e.CancelCommand();
        HideCompletions();
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !CommandParser.IsBlankOrComment(l))
            .ToList();
        _ = RunLinesAsync(lines);
    }

    private async Task RunLinesAsync(List<string> lines)
    {
        foreach (var line in lines)
        {
            await ExecuteAndFlushAsync(line, "手动");
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }
}
