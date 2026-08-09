using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Shell.Mcp;
using HistoryVulcan.Shell.Views;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ModuleCatalogSnapshotTests
{
    [Fact]
    public void SnapshotFiltersBySourceInsteadOfCommandPrefix()
    {
        var modules = new[] { Module("Math", 2) };
        var commands = new[]
        {
            Command("calc.add", "Math"),
            Command("calc.subtract", "Math"),
            new ModuleCommandInfo("Math.localOnly", "wrong source", "", "app", null),
        };

        Assert.True(ModuleCatalogSnapshot.TryCreate(
            modules, commands, out var snapshot, out var error), error);
        Assert.Equal(
            ["calc.add", "calc.subtract"],
            snapshot!.CommandsFor("math").Select(command => command.Name));
    }

    [Fact]
    public void SnapshotRejectsCrossGenerationCounts()
    {
        var modules = new[] { Module("Math", 2) };

        Assert.False(ModuleCatalogSnapshot.TryCreate(
            modules, [Command("calc.add", "Math")], out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Contains("刷新期间发生变化", error);
    }

    [Fact]
    public async Task ReaderUsesRemoteModuleAndCommandCatalogsTogether()
    {
        var calls = new List<string>();
        var bus = new CommandBus(new CommandRegistry(), new TestLog())
        {
            RemoteExecutor = (text, _, _) =>
            {
                calls.Add(text);
                return Task.FromResult(text == "vulcan.module.list"
                    ? CommandResult.Ok("modules", new List<ModuleMeta> { Module("Math", 1) })
                    : CommandResult.Ok("commands", new List<CommandCatalogRow>
                    {
                        CatalogRow("calc.add", "Math"),
                    }));
            },
        };

        var result = await ModuleCatalogReader.LoadAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["vulcan.module.list", "vulcan.command.list"], calls);
        Assert.Equal("calc.add", Assert.Single(result.Snapshot!.CommandsFor("Math")).Name);
    }

    [Fact]
    public async Task ReaderDeserializesJsonDataReturnedAcrossProcessBoundary()
    {
        var modules = new List<ModuleMeta> { Module("Math", 1) };
        var commands = new List<CommandCatalogRow> { CatalogRow("calc.add", "Math") };
        var bus = new CommandBus(new CommandRegistry(), new TestLog())
        {
            RemoteExecutor = (text, _, _) => Task.FromResult(text == "vulcan.module.list"
                ? CommandResult.Ok("modules", JsonSerializer.SerializeToElement(modules))
                : CommandResult.Ok("commands", JsonSerializer.SerializeToElement(commands))),
        };

        var result = await ModuleCatalogReader.LoadAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal("calc.add", Assert.Single(result.Snapshot!.CommandsFor("Math")).Name);
    }

    [Fact]
    public async Task ModulesViewReaderDoesNotRequireCommandCatalogConsistency()
    {
        var calls = new List<string>();
        var modules = new List<ModuleMeta> { Module("HistoryJanus", 31) };
        var bus = new CommandBus(new CommandRegistry(), new TestLog())
        {
            RemoteExecutor = (text, _, _) =>
            {
                calls.Add(text);
                return Task.FromResult(text == "vulcan.module.list"
                    ? CommandResult.Ok("modules", JsonSerializer.SerializeToElement(modules))
                    : CommandResult.Fail("command catalog is intentionally unavailable"));
            },
        };

        var result = await ModuleCatalogReader.LoadModulesAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["vulcan.module.list"], calls);
        Assert.Equal("HistoryJanus", Assert.Single(result.Snapshot!.Modules).ModuleName);
        Assert.Empty(result.Snapshot.Commands);
    }

    [Fact]
    public void ModulesViewUsesOneRefreshActionAndNoCommandDetailPane()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "b-Code-HistoryVulcan",
            "src",
            "HistoryVulcan.Shell",
            "Views",
            "ModulesView.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var elements = document.Descendants().ToList();
        var buttons = elements.Where(element => element.Name == presentation + "Button").ToList();

        Assert.Single(buttons, button => (string?)button.Attribute("Click") == "OnReloadClick");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "刷新模块");
        Assert.DoesNotContain(buttons, button => (string?)button.Attribute("Click") == "OnRefreshClick");
        Assert.DoesNotContain(elements, element => (string?)element.Attribute(x + "Name") == "CommandList");
        Assert.DoesNotContain(elements, element => (string?)element.Attribute(x + "Name") == "CommandsTitle");
        Assert.Contains(buttons, button => (string?)button.Attribute(x + "Name") == "OpenDirButton");
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryVulcan 仓库根目录");
    }

    private static ModuleMeta Module(string name, int commandCount)
        => new(name, "", "", "1.0.0", false, name + ".dll", commandCount);

    private static ModuleCommandInfo Command(string name, string module)
        => new(name, name, "", "module", module);

    private static CommandCatalogRow CatalogRow(string name, string module)
        => new(
            name, "calc", name, null, 0, "module", module,
            false, false, null, "standard", false,
            false, null, 0, 0, null);

    private sealed class TestLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
