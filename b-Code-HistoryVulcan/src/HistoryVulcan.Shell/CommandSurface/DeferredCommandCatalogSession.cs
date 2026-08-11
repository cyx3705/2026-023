using HistoryVulcan.Extensibility.CommandSurface;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Shell.CommandSurface;

/// <summary>
/// Console-side proxy created before Mercury CreateUi. Forwards to the real session after Attach.
/// 未挂接真实会话时（无 Mercury 的宿主）不再整体失效：域/类分类直接回退到本地
/// <see cref="CommandRegistry"/>，使 <c>vulcan.log.source</c> / <c>vulcan.log.class</c>
/// 这类控制台自有过滤在缺少命令工作台时仍然可用（DEC-023）。
/// 补全、检索和选择仍需 Mercury，回退实现只覆盖分类。
/// </summary>
internal sealed class DeferredCommandCatalogSession : ICommandCatalogSession
{
    private const string All = "全部";

    private readonly object _gate = new();
    private readonly CommandRegistry? _registry;
    private ICommandCatalogSession? _inner;
    private string _localDomain = All;
    private string _localClass = All;

    /// <param name="registry">
    /// 本地权威注册表；为 null 时退化为 3.3.1 之前的纯代理行为（仅用于不关心分类的测试）。
    /// </param>
    public DeferredCommandCatalogSession(CommandRegistry? registry = null) => _registry = registry;

    private EventHandler<CommandCatalogChangedEventArgs>? _changed;
    private bool _disposed;

    public event EventHandler<CommandCatalogChangedEventArgs>? Changed
    {
        add
        {
            _changed += value;
            var inner = Inner;
            if (inner != null)
                inner.Changed += value;
        }
        remove
        {
            _changed -= value;
            var inner = Inner;
            if (inner != null)
                inner.Changed -= value;
        }
    }

    public IReadOnlyList<string> Domains => Inner?.Domains ?? LocalDomains();

    public IReadOnlyList<string> Classes => Inner?.Classes ?? LocalClasses();

    public string? SelectedCommandName => Inner?.SelectedCommandName;

    public CommandCatalogFilter CurrentFilter
        => Inner?.CurrentFilter ?? new CommandCatalogFilter(Domain: _localDomain, CommandClass: _localClass);

    private ICommandCatalogSession? Inner
    {
        get { lock (_gate) return _inner; }
    }

    public void Attach(ICommandCatalogSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        EventHandler<CommandCatalogChangedEventArgs>? handlers;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (ReferenceEquals(_inner, session))
                return;

            // do not dispose — owned by Mercury CommandSurfaceFeature
            if (_inner != null && _changed != null)
                _inner.Changed -= OnInnerChanged;

            _inner = session;
            handlers = _changed;
            if (handlers != null)
                _inner.Changed += OnInnerChanged;
        }

        handlers?.Invoke(this, new CommandCatalogChangedEventArgs(CommandCatalogChangeKind.Invalidated));
    }

    public Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        => Inner?.RefreshAsync(force, cancellationToken) ?? Task.FromResult(false);

    public void SetFilter(CommandCatalogFilter filter) => Inner?.SetFilter(filter);

    public bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains)
    {
        var inner = Inner;
        if (inner != null)
            return inner.TrySetDomain(domain, out availableDomains);

        availableDomains = LocalDomainChoices();
        if (!availableDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return false;

        _localDomain = Canonical(availableDomains, domain);
        // 严格两级：域回到「全部」时类必须同步收敛（DEC-021）。
        if (_localDomain == All)
            _localClass = All;
        else if (!LocalClassChoices().Contains(_localClass, StringComparer.OrdinalIgnoreCase))
            _localClass = All;
        _changed?.Invoke(this, new CommandCatalogChangedEventArgs(CommandCatalogChangeKind.Filter));
        return true;
    }

    public bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses)
    {
        var inner = Inner;
        if (inner != null)
            return inner.TrySetCommandClass(commandClass, out availableClasses);

        availableClasses = LocalClassChoices();
        if (!availableClasses.Contains(commandClass, StringComparer.OrdinalIgnoreCase))
            return false;

        _localClass = Canonical(availableClasses, commandClass);
        _changed?.Invoke(this, new CommandCatalogChangedEventArgs(CommandCatalogChangeKind.Filter));
        return true;
    }

    private IReadOnlyList<string> LocalDomains()
        => _registry == null
            ? []
            : _registry.All()
                .Select(command => _registry.GetDomain(command.Name))
                .Where(domain => !string.IsNullOrWhiteSpace(domain))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(domain => domain, StringComparer.Ordinal)
                .ToList();

    private IReadOnlyList<string> LocalClasses()
    {
        if (_registry == null || _localDomain == All)
            return [];

        // DEC-025：无类直接方法参与筛选，用「无类」标签代表空串类键，并排在具体类之后。
        return _registry.All()
            .Where(command => _registry.GetDomain(command.Name)
                .Equals(_localDomain, StringComparison.OrdinalIgnoreCase))
            .Select(command => CommandClassLabels.Display(_registry.GetCommandClass(command.Name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(commandClass => commandClass == CommandClassLabels.None ? 1 : 0)
            .ThenBy(commandClass => commandClass, StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> LocalDomainChoices() => [All, .. LocalDomains()];

    private IReadOnlyList<string> LocalClassChoices() => [All, .. LocalClasses()];

    private static string Canonical(IReadOnlyList<string> choices, string value)
        => choices.FirstOrDefault(
               choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase))
           ?? value;

    public void SetConsoleQuery(string query) => Inner?.SetConsoleQuery(query);

    public bool MoveSelection(int direction) => Inner?.MoveSelection(direction) ?? false;

    public void Select(string? commandName) => Inner?.Select(commandName);

    public Task<ConsoleCompletionResult> CompleteAsync(
        string text,
        int caretIndex,
        CancellationToken cancellationToken = default)
        => Inner?.CompleteAsync(text, caretIndex, cancellationToken)
           ?? Task.FromResult(ConsoleCompletionResult.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_inner != null)
            {
                if (_changed != null)
                    _inner.Changed -= OnInnerChanged;
                // Session lifetime belongs to Mercury CommandSurfaceFeature.
                _inner = null;
            }
        }
    }

    private void OnInnerChanged(object? sender, CommandCatalogChangedEventArgs e)
        => _changed?.Invoke(this, e);
}
