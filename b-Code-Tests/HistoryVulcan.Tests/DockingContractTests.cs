
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Shell;
using HistoryVulcan.Shell.Docking;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(TestCollections.Ui)]
public sealed class DockingContractTests
{
    [Fact]
    public void DockSideKeepsLegacyTabValue()
    {
        Assert.Equal(0, (int)DockSide.Left);
        Assert.Equal(1, (int)DockSide.Right);
        Assert.Equal(2, (int)DockSide.Top);
        Assert.Equal(3, (int)DockSide.Bottom);
        Assert.Equal(4, (int)DockSide.Tab);
        Assert.Equal(5, (int)DockSide.Center);
    }

    [Fact]
    public void ToolWindowDefaultsToRightAndModulesCanOverridePlacement()
    {
        var defaultWindow = new ToolWindowDescriptor
        {
            Id = "module.default",
            Title = "module.default",
        };
        var overriddenWindow = new ToolWindowDescriptor
        {
            Id = "module.override",
            Title = "module.override",
            DefaultSide = DockSide.Top,
        };

        Assert.Equal(DockSide.Right, defaultWindow.DefaultSide);
        Assert.Equal(0.25, defaultWindow.DefaultRatio);
        Assert.Equal(DockSide.Top, overriddenWindow.DefaultSide);
    }

    [Fact]
    public void CenterIsExplicitAndUsesTheMainDocumentPane()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [Tool("stage", DockSide.Center, 1)],
                new MemoryLayoutStore(),
                new NullLog());

            host.Initialize();

            var state = Assert.Single(host.ListWindows());
            Assert.Equal(DockSide.Center, state.Side);
            Assert.Null(state.Ratio);
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            var anchorable = Assert.Single(pane.Children.OfType<LayoutAnchorable>());
            Assert.Equal("stage", anchorable.ContentId);
            Assert.True(anchorable.CanDockAsTabbedDocument);
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutDocument>(),
                item => item.ContentId == "stage");
            Assert.Equal(GridUnitType.Star, pane.DockWidth.GridUnitType);
            var dock = FrontendCommandCatalog.FrameworkSourceDescriptors.Single(item => item.Name == "vulcan.ui.dock");
            Assert.Contains("center", dock.Parameters.Single(item => item.Name == "pos").AllowedValues!);
        });
    }

    [Fact]
    public void LastCenterCarrierCannotBeHiddenOrFloated()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                new MemoryLayoutStore(),
                new NullLog());

            host.Initialize();
            host.Hide(StandardWindowIds.Mcp);
            var afterHide = Assert.Single(host.ListWindows());
            Assert.True(afterHide.IsVisible);
            Assert.False(afterHide.IsFloating);
            Assert.Equal(DockSide.Center, afterHide.Side);

            host.Float(StandardWindowIds.Mcp);
            var afterFloat = Assert.Single(host.ListWindows());
            Assert.True(afterFloat.IsVisible);
            Assert.False(afterFloat.IsFloating);
            Assert.Equal(DockSide.Center, afterFloat.Side);

            host.Dock(StandardWindowIds.Mcp, DockSide.Right, 0.25);
            var afterDock = Assert.Single(host.ListWindows());
            Assert.True(afterDock.IsVisible);
            Assert.False(afterDock.IsFloating);
            Assert.Equal(DockSide.Center, afterDock.Side);
        });
    }

    [Fact]
    public void RemovingLastBusinessCenterRestoresCommandCatalog()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                new MemoryLayoutStore(),
                new NullLog());

            host.Initialize();
            Assert.Equal(DockSide.Center, host.ListWindows().Single(w => w.Id == "business").Side);

            host.UnregisterWindow("business");

            var commandCatalog = Assert.Single(host.ListWindows());
            Assert.Equal(StandardWindowIds.Mcp, commandCatalog.Id);
            Assert.True(commandCatalog.IsVisible);
            Assert.False(commandCatalog.IsFloating);
            Assert.Equal(DockSide.Center, commandCatalog.Side);
        });
    }

    [Fact]
    public void RestoreMovesLegacyCommandCatalogIntoEmptyCenter()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var oldManager = new DockingManager();
            var emptyBackground = new LayoutDocumentPane
            {
                DockWidth = new GridLength(0.01, GridUnitType.Pixel),
            };
            var oldCommandCatalog = new LayoutAnchorable
            {
                ContentId = StandardWindowIds.Mcp,
                Title = "命令集",
                Content = new Border(),
                CanClose = false,
                CanDockAsTabbedDocument = false,
            };
            var oldCenterPane = new LayoutAnchorablePane(oldCommandCatalog) { DockWidth = new GridLength(1, GridUnitType.Star) };
            var oldCenterRegion = new LayoutPanel(emptyBackground) { Orientation = Orientation.Horizontal };
            oldCenterRegion.Children.Add(oldCenterPane);
            var oldCenterColumn = new LayoutPanel(oldCenterRegion) { Orientation = Orientation.Vertical };
            oldManager.Layout = new LayoutRoot
            {
                RootPanel = new LayoutPanel(oldCenterColumn) { Orientation = Orientation.Horizontal },
            };
            using (var writer = new StringWriter())
            {
                new XmlLayoutSerializer(oldManager).Serialize(writer);
                store.WriteCurrent(writer.ToString());
            }

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            host.Initialize();

            var commandCatalog = Assert.Single(host.ListWindows());
            Assert.True(commandCatalog.IsVisible);
            Assert.Equal(DockSide.Center, commandCatalog.Side);
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            Assert.Equal(
                StandardWindowIds.Mcp,
                Assert.Single(pane.Children.OfType<LayoutDocument>()).ContentId);
            Assert.Equal(GridUnitType.Star, pane.DockWidth.GridUnitType);
        });
    }

    [Fact]
    public void RestoreNestedLegacyLayoutMovesOnlyCommandCatalog()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var legacyWindow = ShowHost(
                [
                    Tool("resource", DockSide.Left, 0.18),
                    Tool("legacy-command-catalog", DockSide.Right, 0.3),
                    Tool("details", DockSide.Right, 0.3),
                    Tool("console", DockSide.Bottom, 0.28),
                ], store, out var legacyHost);
            try
            {
                UiTestHost.Pump();
                legacyHost.SaveCurrentLayout();
                store.ReplaceCurrent("legacy-command-catalog", StandardWindowIds.Mcp);
            }
            finally
            {
                legacyWindow.Close();
            }

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool("resource", DockSide.Left, 0.18),
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.3),
                    Tool("console", DockSide.Bottom, 0.28),
                ],
                store,
                new NullLog());
            host.Initialize();

            var windows = host.ListWindows().ToDictionary(window => window.Id);
            Assert.Equal(DockSide.Left, windows["resource"].Side);
            Assert.Equal(DockSide.Center, windows[StandardWindowIds.Mcp].Side);
            Assert.Equal(DockSide.Right, windows["details"].Side);
            Assert.Equal(DockSide.Bottom, windows["console"].Side);
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            Assert.Equal(
                StandardWindowIds.Mcp,
                Assert.Single(pane.Children.OfType<LayoutDocument>()).ContentId);
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutAnchorable>(),
                item => item.ContentId == StandardWindowIds.Mcp);
        });
    }

    // 3.11.3+: module center entries are tool windows hosted in the main document pane.
    [Fact]
    public void ShellWindowHostsSelectablePagesInTheFullMainDocumentPane()
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-center-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var window = new ShellWindow(
                new ShellConfig
                {
                    AppName = "HistoryVulcan Center Test",
                    AppVersion = "3.0.1",
                },
                new MemoryLayoutStore(),
                new NullLog(),
                new MemorySettings(),
                dataDirectory)
            {
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            // DEC-023:中央命令集页由 HistoryMercury 的 CommandSurfaceFeature 提供,不在本仓库门禁内。
            // 这里注册一个等价的中央工具页,断言的是 Vulcan 自己的主文档区几何与页签行为。
            window.Docking.RegisterWindow(Tool(StandardWindowIds.Mcp, DockSide.Center, 1), "test");
            window.Docking.Show(StandardWindowIds.Mcp);

            try
            {
                Assert.Null(window.Mcp);
                Assert.Null(window.Modules);
                Assert.True(window.Commands.Registry.TryGet("vulcan.command.list", out _));
                Assert.False(window.Commands.Registry.TryGet("vulcan.mcp.start", out _));
                Assert.False(window.Commands.Registry.TryGet("vulcan.module.list", out _));
                window.Show();
                UiTestHost.Pump();
                var single = Assert.Single(FindVisualDescendants<LayoutDocumentPaneControl>(window));
                Assert.Single(single.Items);
                Assert.True(single.ActualWidth > window.ActualWidth * 0.5,
                    $"main document width={single.ActualWidth}, window width={window.ActualWidth}");
                Assert.Equal(
                    Visibility.Visible,
                    Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(single)).Visibility);

                window.Docking.RegisterWindow(Tool("business", DockSide.Center, 1), "test");
                window.Docking.Show("business");
                UiTestHost.Pump();
                var multiple = Assert.Single(FindVisualDescendants<LayoutDocumentPaneControl>(window));
                Assert.Equal(2, multiple.Items.Count);
                var centerPane = Assert.IsType<LayoutDocumentPane>(((ILayoutControl)multiple).Model);
                var business = Assert.Single(
                    centerPane.Children.OfType<LayoutAnchorable>(),
                    item => item.ContentId == "business");
                Assert.True(business.IsSelected || business.IsActive);
                Assert.True(multiple.ActualWidth > window.ActualWidth * 0.5,
                    $"main document width={multiple.ActualWidth}, window width={window.ActualWidth}");
                Assert.Equal(
                    Visibility.Visible,
                    Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(multiple)).Visibility);
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void SideToolCanBeDraggedIntoTheMainDocumentPaneAndBackOut()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.25),
                ],
                store,
                new NullLog());
            host.Initialize();

            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            var details = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "details");
            Assert.True(details.CanDockAsTabbedDocument);

            ((ILayoutContainer)details.Parent!).RemoveChild(details);
            pane.Children.Add(details);
            manager.Layout.CollectGarbage();
            UiTestHost.Pump();

            Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == "details").Side);
            Assert.Same(pane, details.Parent);

            host.Dock("details", DockSide.Right, 0.3);
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "details").Side);

            host.Dock("details", DockSide.Center);
            Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == "details").Side);
            Assert.Same(pane, manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "details").Parent);
            host.SaveCurrentLayout();

            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.25),
                ],
                store,
                new NullLog());
            recoveredHost.Initialize();

            Assert.Equal(DockSide.Center, recoveredHost.ListWindows().Single(item => item.Id == "details").Side);
            var recoveredDetails = recoveredManager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "details");
            Assert.True(recoveredDetails.CanDockAsTabbedDocument);
            Assert.IsType<LayoutDocumentPane>(recoveredDetails.Parent);
        });
    }

    [Fact]
    public void LateRegisteredSideToolRestoresIntoCenterWithoutLosingToolIdentity()
    {
        UiTestHost.RunSta(() =>
        {
            var settings = new MemorySettings();
            settings.Set(
                "layout.placements",
                "{\"module.first\":{\"Side\":5,\"Ratio\":0.25,\"Hidden\":false,\"TabTarget\":null," +
                "\"CenterIndex\":2,\"Selected\":false}," +
                "\"module.second\":{\"Side\":5,\"Ratio\":0.25,\"Hidden\":false,\"TabTarget\":null," +
                "\"CenterIndex\":1,\"Selected\":false}}");
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                new MemoryLayoutStore(),
                new NullLog(),
                settings);
            host.Initialize();

            host.RegisterWindow(Tool("module.first", DockSide.Right, 0.25), "module:test");
            host.RegisterWindow(Tool("module.second", DockSide.Right, 0.25), "module:test");

            Assert.All(
                host.ListWindows().Where(item => item.Id.StartsWith("module.", StringComparison.Ordinal)),
                item => Assert.Equal(DockSide.Center, item.Side));
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            Assert.Equal(
                [StandardWindowIds.Mcp, "module.second", "module.first"],
                pane.Children.Select(item => item.ContentId));
            Assert.Equal(StandardWindowIds.Mcp, pane.SelectedContent?.ContentId);
            var restored = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Where(item => item.ContentId?.StartsWith("module.", StringComparison.Ordinal) == true)
                .ToList();
            Assert.Equal(2, restored.Count);
            Assert.All(restored, item =>
            {
                Assert.Same(pane, item.Parent);
                Assert.True(item.CanDockAsTabbedDocument);
            });
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutDocument>(),
                item => item.ContentId?.StartsWith("module.", StringComparison.Ordinal) == true);

            host.Dock("module.first", DockSide.Right, 0.3);
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "module.first").Side);
        });
    }

    [Fact]
    public void LateRegisteredDefaultModuleWindowJoinsExistingRightPane()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool(StandardWindowIds.CommandDetail, DockSide.Right, 0.32),
                    Tool(StandardWindowIds.Modules, DockSide.Right, 0.32),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();

            host.RegisterWindow(new ToolWindowDescriptor
            {
                Id = "module.registered",
                Title = "module.registered",
                ContentFactory = () => new Border(),
            }, "module:test");

            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutAnchorablePane>());
            Assert.Equal(
                [StandardWindowIds.CommandDetail, StandardWindowIds.Modules, "module.registered"],
                pane.Children.Select(item => item.ContentId));
            Assert.Equal("module.registered", pane.SelectedContent?.ContentId);
            var registered = host.ListWindows().Single(item => item.Id == "module.registered");
            Assert.Equal(DockSide.Right, registered.Side);
            Assert.Equal("module:test", registered.Owner);

            host.ResetWindow("module.registered");
            Assert.Same(
                pane,
                manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "module.registered").Parent);
        });
    }

    [Fact]
    public void NamedLayoutPreservesHiddenBusinessCenterPage()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();
            host.Hide("business");
            host.SaveLayout("hidden-center");

            host.Show("business");
            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);

            Assert.True(host.LoadLayout("hidden-center"));
            Assert.False(host.ListWindows().Single(item => item.Id == "business").IsVisible);
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutContent>(),
                item => item.ContentId == "business" && item.Parent is LayoutDocumentPane);
        });
    }

    [Fact]
    public void FloatingCenterPageRoundTripsWithoutInvalidatingMainLayout()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("business", DockSide.Center, 1),
            };
            var window = ShowHost(descriptors, store, out var host);
            try
            {
                host.Float("business");
                UiTestHost.Pump();
                Assert.True(host.ListWindows().Single(item => item.Id == "business").IsFloating);
                host.SaveCurrentLayout();
            }
            finally
            {
                window.Close();
            }

            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager,
                descriptors,
                store,
                new NullLog());
            recoveredHost.Initialize();

            var recovered = recoveredHost.ListWindows().Single(item => item.Id == "business");
            Assert.True(recovered.IsVisible);
            Assert.True(recovered.IsFloating);
            var mainPane = Assert.Single(
                recoveredManager.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>());
            Assert.Contains(
                mainPane.Children.OfType<LayoutDocument>(),
                item => item.ContentId == StandardWindowIds.Mcp);
        });
    }

    [Fact]
    public void DefaultTabIntoCenterKeepsToolIdentityAcrossResetAndRestore()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                new ToolWindowDescriptor
                {
                    Id = "details",
                    Title = "details",
                    DefaultSide = DockSide.Tab,
                    DefaultTabTarget = StandardWindowIds.Mcp,
                    DefaultRatio = 0.25,
                    ContentFactory = () => new Border(),
                },
            };
            var manager = new DockingManager();
            var host = new DockingHost(manager, descriptors, store, new NullLog());
            host.Initialize();

            AssertCenterTool(manager, "details");
            host.Dock("details", DockSide.Right, 0.3);
            host.ResetWindow("details");
            AssertCenterTool(manager, "details");
            host.SaveCurrentLayout();

            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager, descriptors, store, new NullLog());
            recoveredHost.Initialize();
            AssertCenterTool(recoveredManager, "details");
        });
    }

    [Fact]
    public void RestoredLayoutResolvesCenterTabTargetDeclaredAfterFollower()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var original = new DockingHost(
                new DockingManager(),
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            original.Initialize();
            original.SaveCurrentLayout();

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    new ToolWindowDescriptor
                    {
                        Id = "details",
                        Title = "details",
                        DefaultSide = DockSide.Tab,
                        DefaultTabTarget = "business",
                        DefaultRatio = 0.25,
                        ContentFactory = () => new Border(),
                    },
                    Tool("business", DockSide.Center, 1),
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                ],
                store,
                new NullLog());
            host.Initialize();

            AssertCenterTool(manager, "details");
            Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == "business").Side);
        });
    }

    [Fact]
    public void RuntimeRegistrationResolvesTabTargetDeclaredAfterFollower()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(manager, [], new MemoryLayoutStore(), new NullLog());
            host.Initialize();

            host.RegisterWindow(new ToolWindowDescriptor
            {
                Id = "overview",
                Title = "项目总览",
                DefaultSide = DockSide.Tab,
                DefaultTabTarget = StandardWindowIds.Mcp,
                ContentFactory = () => new Border(),
            }, "HistoryJanus");
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "overview").Side);

            host.RegisterWindow(Tool(StandardWindowIds.Mcp, DockSide.Center, 1), "HistoryMercury");

            AssertCenterTool(manager, "overview");
        });
    }

    [Fact]
    public void RestoredLayoutShowsNewDefaultVisibleCenterPage()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var firstManager = new DockingManager();
            var firstHost = new DockingHost(
                firstManager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            firstHost.Initialize();
            firstHost.SaveCurrentLayout();

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                store,
                new NullLog());
            host.Initialize();

            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);
            // 业务中央页现在是工具窗口而非文档页：位置仍在主文档区，身份是 LayoutAnchorable。
            Assert.Contains(
                manager.Layout.Descendents().OfType<LayoutAnchorable>(),
                item => item.ContentId == "business" && item.Parent is LayoutDocumentPane);
        });
    }

    [Fact]
    public void CurrentLayoutPreservesHiddenCenterPageWithoutSettingsService()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("business", DockSide.Center, 1),
            };
            var first = new DockingHost(
                new DockingManager(),
                descriptors,
                store,
                new NullLog());
            first.Initialize();
            first.Hide("business");
            first.SaveCurrentLayout();

            var manager = new DockingManager();
            var recovered = new DockingHost(manager, descriptors, store, new NullLog());
            recovered.Initialize();

            Assert.False(recovered.ListWindows().Single(item => item.Id == "business").IsVisible);
            Assert.DoesNotContain(
                manager.Layout.RootPanel.Descendents().OfType<LayoutDocument>(),
                item => item.ContentId == "business");
        });
    }

    [Fact]
    public void NamedLayoutShowsDefaultVisibleCenterPageAddedAfterItWasSaved()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var original = new DockingHost(
                new DockingManager(),
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            original.Initialize();
            original.SaveLayout("before-business");

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                store,
                new NullLog());
            host.Initialize();

            Assert.True(host.LoadLayout("before-business"));
            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);
            // 同上：业务中央页以 LayoutAnchorable 呈现。
            Assert.Contains(
                manager.Layout.RootPanel.Descendents().OfType<LayoutAnchorable>(),
                item => item.ContentId == "business");
        });
    }

    [Fact]
    public void NonFiniteRatiosAreRejectedByDockingApiAndCommands()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var log = new NullLog();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.25),
                ],
                new MemoryLayoutStore(),
                log);
            host.Initialize();

            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => host.SetRatio("details", invalid));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => host.Dock("details", DockSide.Right, invalid));
            }

            var registry = new CommandRegistry();
            var bus = new CommandBus(registry, log);
            BuiltinCommands.Register(registry, new ShellCommandServices
            {
                Window = null!,
                Docking = host,
                Console = null!,
                History = null!,
                Settings = new MemorySettings(),
                Log = log,
                Bus = bus,
                DataDirectory = "",
            });

            foreach (var value in new[] { "NaN", "Infinity", "-Infinity" })
            {
                var dock = bus.ExecuteAsync(
                    $"vulcan.ui.dock name=details pos=right ratio={value}",
                    "Test").GetAwaiter().GetResult();
                var ratio = bus.ExecuteAsync(
                    $"vulcan.ui.ratio name=details value={value}",
                    "Test").GetAwaiter().GetResult();
                Assert.False(dock.Success);
                Assert.False(ratio.Success);
                Assert.Contains("严格位于 (0,1)", dock.Message, StringComparison.Ordinal);
                Assert.Contains("严格位于 (0,1)", ratio.Message, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void NewWindowOnRestoredLayoutKeepsDeclaredRatio()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var first = ShowHost(
                [Tool("existing", DockSide.Right, 0.25)], store, out var firstHost);
            try
            {
                UiTestHost.Pump();
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var second = ShowHost(
                [
                    Tool("existing", DockSide.Right, 0.25),
                    Tool("stage", DockSide.Top, 0.4),
                ], store, out var secondHost);
            try
            {
                UiTestHost.Pump();
                var ratio = secondHost.ListWindows().Single(item => item.Id == "stage").Ratio;
                Assert.NotNull(ratio);
                Assert.InRange(ratio.Value, 0.25, 0.55);
            }
            finally
            {
                second.Close();
            }
        });
    }

    [Fact]
    public void OpposingSidePanesAlwaysReserveTheCenterWorkspace()
    {
        UiTestHost.RunSta(() =>
        {
            var window = ShowHost(
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("left", DockSide.Left, 0.18),
                    Tool("right", DockSide.Right, 0.38),
                ], new MemoryLayoutStore(), out var host);
            try
            {
                UiTestHost.Pump();
                host.SetRatio("left", 0.55);
                host.SetRatio("right", 0.55);
                UiTestHost.Pump();

                var windows = host.ListWindows().ToDictionary(item => item.Id);
                var sides = windows["left"].Ratio!.Value + windows["right"].Ratio!.Value;
                Assert.InRange(sides, 0.48, 0.51);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MainWindowResizeDoesNotBecomeANewSplitterGesture()
    {
        UiTestHost.RunSta(() =>
        {
            var window = ShowHost(
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("left", DockSide.Left, 0.2),
                    Tool("right", DockSide.Right, 0.3),
                ], new MemoryLayoutStore(), out var host);
            try
            {
                UiTestHost.Pump();
                var before = host.ListWindows().ToDictionary(item => item.Id);
                window.Width = 620;
                // DockingHost 的 _resizeDebounce 是 200ms：必须让它真正到期，
                // 才能断言「尺寸变化没有被当成拖动分隔条」的最终比例。
                UiTestHost.PumpFor(250);
                var after = host.ListWindows().ToDictionary(item => item.Id);

                Assert.InRange(
                    Math.Abs(after["left"].Ratio!.Value - before["left"].Ratio!.Value), 0, 0.03);
                Assert.InRange(
                    Math.Abs(after["right"].Ratio!.Value - before["right"].Ratio!.Value), 0, 0.03);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RestoredOversubscribedSidePanesAreNormalized()
    {
        UiTestHost.RunSta(() =>
        {
            var tools = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("left", DockSide.Left, 0.2),
                Tool("right", DockSide.Right, 0.3),
            };
            var store = new MemoryLayoutStore();
            var first = ShowHost(tools, store, out var firstHost);
            try
            {
                UiTestHost.Pump();
                var manager = (DockingManager)first.Content;
                foreach (var pane in manager.Layout.Descendents().OfType<LayoutAnchorablePane>()
                             .Where(pane => pane.Children.Any(item =>
                                 item.ContentId is "left" or "right")))
                {
                    pane.DockWidth = new GridLength(480, GridUnitType.Pixel);
                }
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var second = ShowHost(tools, store, out var secondHost);
            try
            {
                UiTestHost.Pump();
                UiTestHost.Pump();
                var windows = secondHost.ListWindows().ToDictionary(item => item.Id);
                var sides = windows["left"].Ratio!.Value + windows["right"].Ratio!.Value;
                Assert.InRange(sides, 0.48, 0.51);
            }
            finally
            {
                second.Close();
            }
        });
    }

    [Fact]
    public void RestoredDuplicatePanesOnTheSameSideBecomeOneTabGroup()
    {
        UiTestHost.RunSta(() =>
        {
            var tools = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("right.one", DockSide.Right, 0.25),
                Tool("right.two", DockSide.Right, 0.25),
            };
            var store = new MemoryLayoutStore();
            var first = ShowHost(tools, store, out var firstHost);
            try
            {
                UiTestHost.Pump();
                var manager = (DockingManager)first.Content;
                var second = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "right.two");
                var original = Assert.IsType<LayoutAnchorablePane>(second.Parent);
                original.Children.Remove(second);
                manager.Layout.RootPanel.Children.Add(new LayoutAnchorablePane(second)
                {
                    DockWidth = new GridLength(160, GridUnitType.Pixel),
                });
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var restored = ShowHost(tools, store, out _);
            try
            {
                UiTestHost.Pump();
                UiTestHost.Pump();
                var manager = (DockingManager)restored.Content;
                var right = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Where(item => item.ContentId is "right.one" or "right.two")
                    .ToArray();

                Assert.Equal(2, right.Length);
                Assert.Same(right[0].Parent, right[1].Parent);
            }
            finally
            {
                restored.Close();
            }
        });
    }

    private static Window ShowHost(
        IReadOnlyList<ToolWindowDescriptor> tools,
        MemoryLayoutStore store,
        out DockingHost host)
    {
        var manager = new DockingManager();
        host = new DockingHost(manager, tools, store, new NullLog());
        host.Initialize();
        var window = new Window
        {
            Width = 1000,
            Height = 700,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = manager,
        };
        window.Show();
        manager.UpdateLayout();
        return window;
    }

    private static void AssertCenterTool(DockingManager manager, string id)
    {
        var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
        var anchorable = manager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Single(item => item.ContentId == id);
        Assert.Same(pane, anchorable.Parent);
        Assert.True(anchorable.CanDockAsTabbedDocument);
        Assert.DoesNotContain(
            manager.Layout.Descendents().OfType<LayoutDocument>(),
            item => item.ContentId == id);
    }

    private static ToolWindowDescriptor Tool(string id, DockSide side, double ratio) => new()
    {
        Id = id,
        Title = id,
        DefaultSide = side,
        DefaultRatio = ratio,
        ContentFactory = () => new Border(),
    };

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public void ReplaceCurrent(string oldValue, string newValue)
            => _current = _current?.Replace(oldValue, newValue, StringComparison.Ordinal);
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
