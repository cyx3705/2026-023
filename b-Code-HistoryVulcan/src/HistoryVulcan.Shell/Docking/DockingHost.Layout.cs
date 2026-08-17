using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace HistoryVulcan.Shell.Docking;

public sealed partial class DockingHost
{
    private void BuildDefaultLayout()
    {
        _preserveDefaultRatioOnSeed.Clear();
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        var docPane = new LayoutDocumentPane();

        // 中央主区使用 AvalonDock 原生文档窗格。命令集保留文档身份；模块和消费方
        // 的 Center 窗口以工具窗口身份挂入同一主区，避免退役的文档页注册语义回流。
        var centerColumn = new LayoutPanel(docPane) { Orientation = Orientation.Vertical };
        var rootPanel = new LayoutPanel(centerColumn) { Orientation = Orientation.Horizontal };
        var root = new LayoutRoot { RootPanel = rootPanel };
        _manager.Layout = root;

        double leftRatio = 0, rightRatio = 0, topRatio = 0, bottomRatio = 0;

        LayoutAnchorablePane MakeSidePane(IEnumerable<ToolWindowDescriptor> group)
        {
            var pane = new LayoutAnchorablePane();
            foreach (var d in group)
                pane.Children.Add(CreateAnchorable(d));
            return pane;
        }

        var bySide = _descriptors
            .Where(d => d.DefaultSide is not DockSide.Tab and not DockSide.Center)
            .GroupBy(d => d.DefaultSide)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (bySide.TryGetValue(DockSide.Left, out var lefts))
        {
            leftRatio = lefts.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(lefts);
            pane.DockWidth = Star(leftRatio);
            rootPanel.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Right, out var rights))
        {
            rightRatio = rights.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(rights);
            pane.DockWidth = Star(rightRatio);
            rootPanel.Children.Add(pane);
        }

        if (bySide.TryGetValue(DockSide.Top, out var tops))
        {
            topRatio = tops.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(tops);
            pane.DockHeight = Star(topRatio);
            centerColumn.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Bottom, out var bottoms))
        {
            bottomRatio = bottoms.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(bottoms);
            pane.DockHeight = Star(bottomRatio);
            centerColumn.Children.Add(pane);
        }

        foreach (var descriptor in _descriptors
                     .Where(d => d.DefaultSide == DockSide.Center)
                     .OrderByDescending(d => IsPrimaryCommandDocument(d.Id)))
        {
            if (UsesDocumentIdentity(descriptor))
            {
                var document = CreateDocument(descriptor);
                if (descriptor.DefaultVisible)
                    docPane.Children.Add(document);
                else
                    _hiddenCenterIds.Add(descriptor.Id);
            }
            else
            {
                var anchorable = CreateAnchorable(descriptor);
                if (descriptor.DefaultVisible)
                    docPane.Children.Add(anchorable);
                else
                    _hiddenCenterIds.Add(descriptor.Id);
            }
        }

        centerColumn.DockWidth = Star(Math.Max(1 - leftRatio - rightRatio, 0.1));
        docPane.DockHeight = Star(Math.Max(1 - topRatio - bottomRatio, 0.1));

        // 第二遍:并入标签组的窗口(DefaultSide = Tab)
        foreach (var d in _descriptors.Where(d => d.DefaultSide == DockSide.Tab))
        {
            var documentTarget = d.DefaultTabTarget != null
                ? FindCenterDocument(d.DefaultTabTarget)
                : null;
            if (documentTarget?.Parent is LayoutDocumentPane documentPane)
            {
                documentPane.Children.Add(CreateAnchorable(d));
                continue;
            }

            var a = CreateAnchorable(d);
            var target = d.DefaultTabTarget != null ? FindAnchorable(d.DefaultTabTarget) : null;
            if (target?.Parent is LayoutAnchorablePane tp)
            {
                tp.Children.Add(a);
            }
            else
            {
                _log.Warn(LayoutSource, $"窗口 {d.Id} 的默认标签组目标 {d.DefaultTabTarget ?? "(空)"} 不存在,改为右侧停靠");
                var pane = new LayoutAnchorablePane(a)
                {
                    DockWidth = Star(NormalizeRatio(d.DefaultRatio, 0.25)),
                };
                rootPanel.Children.Add(pane);
            }
        }

        // 默认隐藏的窗口
        foreach (var d in _descriptors.Where(d => !d.DefaultVisible))
            FindAnchorable(d.Id)?.Hide();

        root.CollectGarbage();
    }

    private string SerializeLayout()
    {
        // A close/save can race the 500 ms gesture debounce. Preserve any tool page that the
        // user intentionally embedded in the central document pane.
        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
        }
        if (!LayoutHasMainDocumentPane())
            throw new InvalidOperationException("布局中必须且只能存在一个中央主文档区");

        using var writer = new StringWriter();
        new XmlLayoutSerializer(_manager).Serialize(writer);
        var document = XDocument.Parse(writer.ToString(), LoadOptions.PreserveWhitespace);
        var hiddenIds = _hiddenCenterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var metadata = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(hiddenIds));
        document.AddFirst(new XComment(HiddenCenterMetadataPrefix + metadata));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private void ApplyLayoutXml(string xml)
    {
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        xml = ExtractHiddenCenterMetadata(xml);
        var serializer = new XmlLayoutSerializer(_manager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            var contentId = e.Model.ContentId;
            if (e.Model is LayoutDocument document &&
                contentId != null && _byId.TryGetValue(contentId, out var documentDescriptor))
            {
                if (!UsesDocumentIdentity(documentDescriptor))
                {
                    e.Cancel = true;
                    return;
                }
                document.CanClose = false;
                document.CanFloat = !IsPrimaryCommandDocument(contentId);
                document.Title = documentDescriptor.Title;
                e.Content = GetOrCreateContent(documentDescriptor);
                _centerDocuments[contentId] = document;
            }
            else if (e.Model is LayoutAnchorable anchorable &&
                     contentId != null && _byId.TryGetValue(contentId, out var d))
            {
                // 3.0 早期候选把 Center 做成与空文档区并排的工具窗。取消该旧节点，
                // EnsureRegisteredWindows 会把默认中央窗口迁入真正的文档主区。
                if ((d.DefaultSide == DockSide.Center && !IsInsideFloatingWindow(anchorable)) ||
                    IsPrimaryCommandDocument(contentId))
                {
                    e.Cancel = true;
                    return;
                }
                // Side tool pages may be dragged into the central document pane and back out.
                anchorable.CanDockAsTabbedDocument = true;
                e.Content = GetOrCreateContent(d);
            }
            else
            {
                // 布局文件里有当前版本未注册的窗口 → 丢弃,不阻断加载
                e.Cancel = true;
            }
        };

        using var reader = new StringReader(xml);
        serializer.Deserialize(reader);
    }

    private string ExtractHiddenCenterMetadata(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var metadata = document.Nodes()
            .OfType<XComment>()
            .FirstOrDefault(comment => comment.Value.StartsWith(
                HiddenCenterMetadataPrefix,
                StringComparison.Ordinal));
        if (metadata == null)
            return xml;

        metadata.Remove();
        try
        {
            var encoded = metadata.Value[HiddenCenterMetadataPrefix.Length..];
            var ids = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(encoded)) ?? [];
            foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
                _hiddenCenterIds.Add(id);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _log.Warn(LayoutSource, $"隐藏中央页元数据无效,已按默认可见性恢复: {ex.Message}");
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>布局加载后补齐缺失的已注册窗口(旧布局文件兼容)。</summary>
    private void EnsureRegisteredWindows()
    {
        var missing = _descriptors
            .Where(d => FindAnchorable(d.Id) == null && FindCenterDocument(d.Id) == null)
            .ToArray();

        // 先补齐中央文档，再处理 Tab 跟随页，避免恢复结果依赖描述符声明顺序。
        foreach (var descriptor in missing.Where(UsesDocumentIdentity))
            EnsureRegisteredWindow(descriptor);
        foreach (var descriptor in missing.Where(d => !UsesDocumentIdentity(d)))
            EnsureRegisteredWindow(descriptor);
    }

    private void EnsureRegisteredWindow(ToolWindowDescriptor d)
    {
        if (FindAnchorable(d.Id) != null || FindCenterDocument(d.Id) != null)
            return;

        if (UsesDocumentIdentity(d))
        {
            var placement = _orphanPlacements.GetValueOrDefault(d.Id);
            var hidden = _hiddenCenterIds.Contains(d.Id)
                         || !d.DefaultVisible
                         || placement?.Hidden == true;
            var document = MoveToCenterDocument(d);
            if (hidden && !IsPrimaryCommandDocument(d.Id))
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(d.Id);
            }
            else
            {
                ShowCenterDocument(
                    document,
                    placement?.CenterIndex,
                    placement?.Selected ?? false);
            }
        }
        else if (d.DefaultSide == DockSide.Center
                 || (d.DefaultSide == DockSide.Tab && IsCenterTabTarget(d.DefaultTabTarget)))
        {
            // 中央区的工具窗口：位置仍是中央，身份是 LayoutAnchorable。
            // 隐藏态与落位沿用中央页的既有语义，使它与此前的文档页在用户可见行为上一致。
            var placement = _orphanPlacements.GetValueOrDefault(d.Id);
            var hidden = _hiddenCenterIds.Contains(d.Id)
                         || !d.DefaultVisible
                         || placement?.Hidden == true;
            var anchorable = MoveToAnchorable(d);
            ShowAnchorableAsCenterPage(
                anchorable,
                placement?.CenterIndex,
                placement?.Selected ?? false);
            if (hidden)
            {
                anchorable.Hide();
                _hiddenCenterIds.Add(d.Id);
            }
        }
        else
        {
            var anchorable = CreateAnchorable(d);
            var targetPending = d.DefaultSide == DockSide.Tab && d.DefaultTabTarget != null &&
                                !IsCenterTabTarget(d.DefaultTabTarget) &&
                                FindAnchorable(d.DefaultTabTarget)?.Parent is not LayoutAnchorablePane;
            if (targetPending)
            {
                _pendingTabTargets[d.Id] = d.DefaultTabTarget!;
                _log.Info(LayoutSource, $"窗口 {d.Id} 的标签组目标 {d.DefaultTabTarget} 尚未注册，暂时右侧停靠");
            }
            PlaceAtSide(
                anchorable,
                targetPending ? DockSide.Right : d.DefaultSide,
                d.DefaultRatio,
                targetPending ? null : d.DefaultTabTarget);
            if (!d.DefaultVisible)
                anchorable.Hide();
        }
        _preserveDefaultRatioOnSeed.Add(d.Id);
        ResolvePendingTabTargets(d.Id);
    }

    private bool LayoutHasMainDocumentPane()
    {
        var panes = _manager.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>().ToList();
        return panes.Count == 1 && panes[0].Children.OfType<LayoutDocument>().All(document =>
            document.ContentId != null && _byId.ContainsKey(document.ContentId));
    }

    private LayoutAnchorable CreateAnchorable(ToolWindowDescriptor d) => new()
    {
        ContentId = d.Id,
        Title = d.Title,
        Content = GetOrCreateContent(d),
        // §4.1:关闭按钮语义为隐藏,不销毁
        CanClose = false,
        CanHide = true,
        CanAutoHide = true,
        CanFloat = true,
        CanDockAsTabbedDocument = true,
    };

    private LayoutDocument CreateDocument(ToolWindowDescriptor descriptor)
    {
        var document = new LayoutDocument
        {
            ContentId = descriptor.Id,
            Title = descriptor.Title,
            Content = GetOrCreateContent(descriptor),
            CanClose = false,
            CanFloat = !IsPrimaryCommandDocument(descriptor.Id),
        };
        _centerDocuments[descriptor.Id] = document;
        return document;
    }

    private bool IsPrimaryCommandDocument(string id)
        => id.Equals(StandardWindowIds.Mcp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 只有宿主自持的主命令页使用文档身份(<c>LayoutDocument</c>)；其余一律是工具窗口
    /// (<c>LayoutAnchorable</c>)，声明 <see cref="DockSide.Center"/> 的窗口仍落在中央区，
    /// 但以中央页形态呈现(<c>ShowAnchorableAsCenterPage</c>)。
    ///
    /// 为什么模块不再能注册文档页：文档页的拖动行为明显弱于工具窗口，且会引出一连串
    /// 停靠相关缺陷。与其逐个修复文档页的拖动路径，不如取消这条注册路径——**中央位置**
    /// 是模块真正需要的，**文档身份**只是当初取得该位置的手段。
    ///
    /// 旧布局自动迁移：恢复时若存档中的 LayoutDocument 对应的描述符已不再是文档身份，
    /// 反序列化回调会取消该节点，随后由 EnsureRegisteredWindow 以工具窗口重建。
    /// </summary>
    private bool UsesDocumentIdentity(ToolWindowDescriptor descriptor)
        => IsPrimaryCommandDocument(descriptor.Id);

    private LayoutDocumentPane FindMainDocumentPane()
        => _manager.Layout.RootPanel.Descendents()
               .OfType<LayoutDocumentPane>()
               .FirstOrDefault(pane => !IsInsideFloatingWindow(pane))
           ?? throw new InvalidOperationException("布局中找不到中央主文档区");

    private LayoutDocument? FindCenterDocument(string id)
    {
        if (_centerDocuments.TryGetValue(id, out var cached))
            return cached;

        var document = _manager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(item => string.Equals(item.ContentId, id, StringComparison.OrdinalIgnoreCase));
        if (document != null)
            _centerDocuments[id] = document;
        return document;
    }

    private LayoutDocument MoveToCenterDocument(ToolWindowDescriptor descriptor)
    {
        var anchorable = FindAnchorable(descriptor.Id);
        if (anchorable != null)
        {
            Detach(anchorable);
            anchorable.Content = null;
        }

        var document = FindCenterDocument(descriptor.Id) ?? CreateDocument(descriptor);
        _hiddenCenterIds.Remove(descriptor.Id);
        return document;
    }

    private void ShowCenterDocument(LayoutDocument document, int? index = null, bool select = true)
    {
        var pane = FindMainDocumentPane();
        var previousSelection = pane.SelectedContent;
        if (!ReferenceEquals(document.Parent, pane))
        {
            DetachDocument(document);
            var targetIndex = Math.Clamp(index ?? pane.Children.Count, 0, pane.Children.Count);
            pane.Children.Insert(targetIndex, document);
        }
        _hiddenCenterIds.Remove(document.ContentId ?? string.Empty);
        if (select)
        {
            document.IsSelected = true;
            document.IsActive = true;
        }
        else if (previousSelection != null && !ReferenceEquals(previousSelection, document))
        {
            previousSelection.IsSelected = true;
        }
        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
    }

    private void ShowAnchorableAsCenterPage(LayoutAnchorable anchorable, int? index = null, bool select = true)
    {
        var pane = FindMainDocumentPane();
        var previousSelection = pane.SelectedContent;
        if (!ReferenceEquals(anchorable.Parent, pane))
        {
            Detach(anchorable);
            var targetIndex = Math.Clamp(index ?? pane.Children.Count, 0, pane.Children.Count);
            pane.Children.Insert(targetIndex, anchorable);
        }
        anchorable.CanDockAsTabbedDocument = true;
        if (select)
        {
            anchorable.IsSelected = true;
            anchorable.IsActive = true;
        }
        else if (previousSelection != null && !ReferenceEquals(previousSelection, anchorable))
        {
            previousSelection.IsSelected = true;
        }
        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
    }

    private bool IsCenterContent(string id)
        => FindCenterDocument(id)?.Parent is LayoutDocumentPane ||
           FindAnchorable(id) is { } anchorable && IsHostedInDocumentPane(anchorable);

    private bool IsCenterTabTarget(string? id)
        => id != null && IsCenterContent(id);

    private static void NormalizeMainDocumentSizing(LayoutDocumentPane pane)
    {
        pane.DockWidth = Star(1);
        pane.DockHeight = Star(1);

        ILayoutElement current = pane;
        while (current.Parent is LayoutPanel panel && panel.Children.Count == 1)
        {
            SetDockLength(current, panel.Orientation == Orientation.Horizontal, Star(1));
            current = panel;
        }
    }

    private LayoutAnchorable MoveToAnchorable(ToolWindowDescriptor descriptor)
    {
        var document = FindCenterDocument(descriptor.Id);
        if (document != null)
        {
            DetachDocument(document);
            document.Content = null;
            _centerDocuments.Remove(descriptor.Id);
            _hiddenCenterIds.Remove(descriptor.Id);
        }
        return FindAnchorable(descriptor.Id) ?? CreateAnchorable(descriptor);
    }

    private static void DetachDocument(LayoutDocument document)
    {
        if (document.Parent is ILayoutContainer container)
            container.RemoveChild(document);
    }

    private void ScheduleCenterDocumentPresentation()
    {
        if (_presentationRefreshPending)
            return;
        _presentationRefreshPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _presentationRefreshPending = false;
            var mainPane = _manager.Layout.RootPanel.Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault(pane => !IsInsideFloatingWindow(pane));
            if (mainPane == null)
                return;

            foreach (var control in FindVisualDescendants(_manager).OfType<LayoutDocumentPaneControl>())
            {
                if (control is not ILayoutControl { Model: LayoutDocumentPane pane } ||
                    !ReferenceEquals(pane, mainPane))
                    continue;
                foreach (var tabs in FindVisualDescendants(control).OfType<DocumentPaneTabPanel>())
                    tabs.Visibility = Visibility.Visible;
            }
        });
    }

    private void EnsureRegistered(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (FindAnchorable(id) != null || FindCenterDocument(id) != null)
            return;

        if (UsesDocumentIdentity(descriptor))
        {
            ShowCenterDocument(MoveToCenterDocument(descriptor));
            return;
        }

        if (descriptor.DefaultSide == DockSide.Tab && descriptor.DefaultTabTarget != null &&
            FindCenterDocument(descriptor.DefaultTabTarget) != null)
        {
            ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
            return;
        }

        var anchorable = CreateAnchorable(descriptor);
        PlaceAtSide(anchorable, descriptor.DefaultSide, descriptor.DefaultRatio, descriptor.DefaultTabTarget);
    }

    private object GetOrCreateContent(ToolWindowDescriptor d)
    {
        if (!_contents.TryGetValue(d.Id, out var content))
        {
            content = d.ContentFactory?.Invoke()
                      ?? throw new InvalidOperationException(
                          $"窗口 {d.Id} 未提供内容工厂(仅 console 由 Shell 接管内容)");
            _contents.Add(d.Id, content);
        }

        return content;
    }

    internal object? FindContent(string id)
        => _contents.GetValueOrDefault(id);

    private void BuildMaximizedLayout(string id)
    {
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        ILayoutPanelElement pane = UsesDocumentIdentity(_byId[id])
            ? new LayoutDocumentPane(CreateDocument(_byId[id]))
            : new LayoutAnchorablePane(CreateAnchorable(_byId[id]));
        var rootPanel = new LayoutPanel(pane) { Orientation = Orientation.Horizontal };
        _manager.Layout = new LayoutRoot { RootPanel = rootPanel };
    }

    private void TryDispose(object content, string id)
    {
        if (content is not IDisposable disposable)
            return;
        try
        {
            disposable.Dispose();
        }

        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"释放界面内容 {id} 失败: {ex.Message}");
        }
    }

    private void LoadOrphanPlacements()
    {
        var json = _settings?.Get(PlacementSettingsKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            _orphanPlacements = JsonSerializer.Deserialize<Dictionary<string, OrphanPlacement>>(json)
                                ?? new Dictionary<string, OrphanPlacement>(StringComparer.OrdinalIgnoreCase);
            _orphanPlacements = new Dictionary<string, OrphanPlacement>(
                _orphanPlacements, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"读取延迟窗口位置失败: {ex.Message}");
            _orphanPlacements.Clear();
        }
    }

    private void SavePlacements()
    {
        if (_settings == null)
            return;
        var placements = new Dictionary<string, OrphanPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _descriptors)
        {
            var state = ComputeState(descriptor.Id);
            LayoutContent? centerContent = FindCenterDocument(descriptor.Id);
            centerContent ??= FindAnchorable(descriptor.Id) is { } anchorable && IsHostedInDocumentPane(anchorable)
                ? anchorable
                : null;
            var centerPane = centerContent?.Parent as LayoutDocumentPane;
            placements[descriptor.Id] = new OrphanPlacement(
                state.Side ?? descriptor.DefaultSide,
                state.Ratio > 0 ? state.Ratio : _ratios.GetValueOrDefault(descriptor.Id, descriptor.DefaultRatio),
                !state.Visible,
                state.TabTarget,
                centerPane == null ? null : centerPane.Children.IndexOf(centerContent!),
                centerPane == null ? null : ReferenceEquals(centerPane.SelectedContent, centerContent));
        }
        _settings.Set(PlacementSettingsKey, JsonSerializer.Serialize(placements));
    }

    private OrphanPlacement? TakeOrphanPlacement(string id)
    {
        if (!_orphanPlacements.Remove(id, out var placement))
            return null;
        return placement;
    }

    private void ApplyStoredCenterVisibility()
    {
        foreach (var (id, placement) in _orphanPlacements.ToArray())
        {
            var document = FindCenterDocument(id);
            if (document == null)
                continue;

            _orphanPlacements.Remove(id);
            if (placement.Hidden && !IsPrimaryCommandDocument(id))
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(id);
            }
        }
    }

    private sealed record OrphanPlacement(
        DockSide Side,
        double Ratio,
        bool Hidden,
        string? TabTarget,
        int? CenterIndex = null,
        bool? Selected = null);
}

