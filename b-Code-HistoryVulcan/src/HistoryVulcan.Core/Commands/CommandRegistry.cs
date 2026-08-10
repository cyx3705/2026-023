namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 指令注册表(§5.3):框架内置组与派生应用自定义指令并入同一张表,
/// help 自动收录;名称冲突在注册时立即报错,禁止静默覆盖(P0)。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public event Action? Changed;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Register(CommandDescriptor descriptor, string source = "framework")
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("指令来源不能为空", nameof(source));
        if (!string.IsNullOrWhiteSpace(descriptor.CommandClass)
            && !IsValidCommandClass(descriptor.CommandClass.Trim()))
        {
            throw new ArgumentException(
                "命令类必须是以字母开头、只包含字母、数字或连字符的稳定标识符",
                nameof(descriptor));
        }
        lock (_gate)
        {
            if (!_commands.TryAdd(descriptor.Name, descriptor))
                throw new InvalidOperationException($"指令名冲突: {descriptor.Name} 已注册,禁止覆盖(§5.3)");
            _sources[descriptor.Name] = source.Trim();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 注销指令(模块热重载场景:模块 DLL 下线时其指令域随之移除)。
    /// 调用方须只注销自己注册过的名称;存在则移除并返回 true。
    /// [基线 0.4.2 新增,由派生应用 HistoryJanus V2-M3 反哺]
    /// </summary>
    public bool Unregister(string name)
    {
        bool removed;
        lock (_gate)
        {
            _sources.Remove(name);
            removed = _commands.Remove(name);
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool TryGet(string name, out CommandDescriptor descriptor)
    {
        lock (_gate)
            return _commands.TryGetValue(name, out descriptor!);
    }

    /// <summary>返回注册来源：framework / app / module:&lt;name&gt;。</summary>
    public string GetSource(string name)
    {
        lock (_gate)
            return _sources.GetValueOrDefault(name, "framework");
    }

    /// <summary>返回指令的有效域；模块来源始终由模块 owner 决定。</summary>
    public string GetDomain(string name)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(name, out var descriptor))
                return LegacyDomain(name);
            return ResolveDomain(descriptor, _sources.GetValueOrDefault(name, "framework"));
        }
    }

    /// <summary>返回指令在有效域内的功能类。</summary>
    public string GetCommandClass(string name)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(name, out var descriptor))
                return LegacyClass(name);
            return ResolveCommandClass(descriptor, _sources.GetValueOrDefault(name, "framework"));
        }
    }

    /// <summary>全部指令,按名称排序(help 列表)。</summary>
    public IReadOnlyList<CommandDescriptor> All()
    {
        lock (_gate)
            return _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 当前已注册的全部指令域，按序返回。这是域聚焦做绝对名脱固时的**唯一权威源**
    /// （DEC-025）：域清单随模块装载/卸载变化，任何组件都不得硬编码域名字面量。
    /// </summary>
    public IReadOnlyList<string> Domains()
    {
        lock (_gate)
        {
            return _commands.Values
                .Select(command => GetDomainLocked(command.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(domain => domain, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>是否为已注册域；域聚焦据此判断输入首段该按绝对名还是拼前缀解析。</summary>
    public bool IsRegisteredDomain(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var trimmed = candidate.Trim();
        lock (_gate)
        {
            foreach (var command in _commands.Values)
            {
                if (GetDomainLocked(command.Name).Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private string GetDomainLocked(string name)
        => _commands.TryGetValue(name, out var descriptor)
            ? ResolveDomain(descriptor, _sources.GetValueOrDefault(name, "framework"))
            : LegacyDomain(name);

    /// <summary>
    /// 从完整命令名提取一级域。无点号命令本身也是保留域，避免模块以
    /// <c>help.foo</c> 或 <c>future.list</c> 的形式绕过根命令/现有域冲突检查。
    /// </summary>
    public static IReadOnlySet<string> DomainsOf(IEnumerable<string> commandNames)
    {
        ArgumentNullException.ThrowIfNull(commandNames);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in commandNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var trimmed = name.Trim();
            var dot = trimmed.IndexOf('.');
            domains.Add(dot > 0 ? trimmed[..dot] : trimmed);
        }

        return domains;
    }

    internal static string ResolveDomain(CommandDescriptor descriptor, string source)
    {
        if (source.StartsWith("module:", StringComparison.OrdinalIgnoreCase))
        {
            var owner = source["module:".Length..].Trim();
            if (owner.Length > 0)
                return ModuleDomainNaming.ToDomain(owner);
        }

        return string.IsNullOrWhiteSpace(descriptor.Domain)
            ? LegacyDomain(descriptor.Name)
            : descriptor.Domain.Trim();
    }

    internal static string ResolveCommandClass(CommandDescriptor descriptor, string source)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.CommandClass))
            return descriptor.CommandClass.Trim().ToLowerInvariant();
        return LegacyClass(descriptor.Name);
    }

    /// <summary>从命令名推导域：首段；无点则 <c>core</c>。</summary>
    public static string LegacyDomain(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : "core";
    }

    /// <summary>
    /// 从命令名推导功能类，纯结构判定不做语义猜测（DEC-025）：
    /// 三段及以上取第二段；两段 <c>域.方法</c> 是该域的无类直接方法，返回空串；
    /// 单段回退 <c>core</c>。
    /// </summary>
    /// <remarks>
    /// 3.4.0 起两段名不再回退首段作为类。两段名的首段是**域**而不是类，
    /// 把它当类会让 <c>mercury.go</c> 这类指令既占一个域又凭空多出一个同名类。
    /// 空串即「无类」，由显示层翻译成标签，不参与任何类推导。
    /// </remarks>
    public static string LegacyClass(string name)
    {
        var parts = name.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
            return parts[1].ToLowerInvariant();
        if (parts.Length == 2)
            return string.Empty;
        return "core";
    }

    /// <summary>从命令名推导方法段：末段；无点则整名。</summary>
    public static string LegacyMethod(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        return (dot >= 0 ? trimmed[(dot + 1)..] : trimmed).ToLowerInvariant();
    }

    /// <summary>同 <see cref="LegacyMethod"/>。</summary>
    public static string GetMethod(string name) => LegacyMethod(name);

    private static bool IsValidCommandClass(string value)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized.Length == 0 || normalized[0] is < 'a' or > 'z')
            return false;
        return normalized.All(character => character is >= 'a' and <= 'z'
                                           or >= '0' and <= '9'
                                           or '-');
    }

    /// <summary>
    /// 未知指令时给出最接近的候选(§5.2 P1:“你是不是想输入…”)。
    /// 规则:编辑距离 ≤ 2,或同域(点号前缀相同)的全部动作。
    /// </summary>
    public IReadOnlyList<string> Suggest(string unknownName)
    {
        var candidates = new List<(string Name, int Distance)>();
        var dot = unknownName.IndexOf('.');
        var domain = dot > 0 ? unknownName[..(dot + 1)] : null;

        foreach (var name in All().Select(command => command.Name))
        {
            var d = Levenshtein(unknownName.ToLowerInvariant(), name);
            if (d <= 2)
                candidates.Add((name, d));
            else if (domain != null && name.StartsWith(domain, StringComparison.OrdinalIgnoreCase))
                candidates.Add((name, 3));
        }

        return candidates
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name)
            .Take(5)
            .ToList();
    }

    private static int Levenshtein(string a, string b)
    {
        var m = a.Length;
        var n = b.Length;
        var prev = new int[n + 1];
        var cur = new int[n + 1];
        for (var j = 0; j <= n; j++)
            prev[j] = j;

        for (var i = 1; i <= m; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[n];
    }
}
