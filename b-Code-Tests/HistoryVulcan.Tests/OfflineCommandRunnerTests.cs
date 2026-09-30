using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Development;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(TestCollections.HostProcess)]
public sealed class OfflineCommandRunnerTests
{
    [Theory]
    [InlineData("probe.unknown token=private-value")]
    [InlineData("vulcan.module.list token=\"private-value")]
    public void RejectedInputNeverCreatesAnOfflineComposition(string command)
    {
        var created = false;
        var (exit, json) = Run(command, () =>
        {
            created = true;
            throw new InvalidOperationException("must not construct");
        });
        using var envelope = JsonDocument.Parse(json);
        Assert.False(created);
        Assert.Equal(2, exit);
        Assert.Equal(exit, envelope.RootElement.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("private-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionFailureUsesTheSameExitCodeInJsonAndProcessResult()
    {
        var (exit, json) = Run("vulcan.module.list", () => throw new IOException("private-value"));
        using var envelope = JsonDocument.Parse(json);
        Assert.Equal(1, exit);
        Assert.Equal(exit, envelope.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("private-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineRunnerConfiguresOnlyItsOwnDevelopmentContext()
    {
        using var first = Composition();
        using var second = Composition();
        var (exit, _) = Run("vulcan.cli.list", () => first);
        Assert.Equal(0, exit);
        Assert.NotNull(first.Development!.LiveHost);
        Assert.Null(second.Development!.LiveHost);
    }

    [Fact]
    public void HumanOutputPrintsTheMessageWithoutRepeatingDataAsJson()
    {
        using var composition = Composition(registry => registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.list",
            Summary = "test command",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("正文一行", new { OnlyInData = "data-value" })),
        }));

        var (exit, text) = Run("vulcan.module.list", () => composition, HostOutputFormat.Human);
        Assert.Equal(0, exit);
        Assert.Contains("正文一行", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data-value", text, StringComparison.Ordinal);

        using var again = Composition(registry => registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.list",
            Summary = "test command",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("正文一行", new { OnlyInData = "data-value" })),
        }));
        var (_, json) = Run("vulcan.module.list", () => again);
        Assert.Contains("data-value", json, StringComparison.Ordinal);
    }

    private static ServiceComposition Composition(Action<CommandRegistry>? register = null)
    {
        var registry = new CommandRegistry();
        register?.Invoke(registry);
        var log = new NullLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        var development = DevelopmentCommands.Register(registry, bus, settings, Path.GetTempPath());
        foreach (var name in CliExposurePolicy.MissingCommands(registry))
            registry.Register(new CommandDescriptor
            {
                Name = name,
                Summary = "test command",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            });
        return new ServiceComposition
        {
            ServiceName = "test",
            Registry = registry,
            Bus = bus,
            Log = log,
            Settings = settings,
            Development = development,
        };
    }

    private static (int ExitCode, string Json) Run(
        string command, Func<ServiceComposition> compose, HostOutputFormat format = HostOutputFormat.Json)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exit = CommandLineRunner.Run(command, typeof(OfflineCommandRunnerTests).Assembly, format, compose);
            return (exit, output.ToString());
        }
        finally { Console.SetOut(original); }
    }
}
