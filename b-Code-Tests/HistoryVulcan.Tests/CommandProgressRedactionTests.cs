using System.Collections.Concurrent;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class CommandProgressRedactionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task HandlerUnregistrationCannotEraseTheRequestSecretSnapshot(int position)
    {
        var log = new RecordingLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        CommandResult? observed = null;
        bus.Executed += (_, _, result) => observed = result;
        registry.Register(new CommandDescriptor
        {
            Name = "probe.consume",
            Summary = "unregister during execution",
            Parameters = [new ParameterSpec { Name = "password", Description = "secret", Position = position }],
            Handler = CommandDescriptor.Sync(context =>
            {
                var secret = context.RequireString("password");
                registry.Unregister("probe.consume");
                return CommandResult.Ok(secret, new { Secret = secret });
            }),
        });

        var result = await bus.ExecuteAsync("probe.consume private-value", "test");

        Assert.True(result.Success);
        Assert.Equal("[REDACTED]", result.Message);
        Assert.Null(result.Data);
        Assert.Same(result, observed);
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("private-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentProgressMasksNamedAndPositionalSecrets(bool quiet)
    {
        var log = new ProgressLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        registry.Register(new CommandDescriptor
        {
            Name = "probe.report",
            Summary = "progress",
            Parameters = [new ParameterSpec { Name = "token", Description = "secret", Position = 0 }],
            Handler = async context =>
            {
                context.Progress!.Report("start " + context.RequireString("token"));
                await Task.Yield();
                context.Progress.Report("end " + context.RequireString("token"));
                return CommandResult.Ok("finished", new object());
            },
        });
        Task<CommandResult> Run(string text) => quiet ? bus.InvokeAsync(text, "test") : bus.ExecuteAsync(text, "test");
        var results = await Task.WhenAll(Run("probe.report token=named-secret"), Run("probe.report positional-secret"));
        await log.Complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var progress = log.Entries.Where(entry => entry.Category.StartsWith("cmd:progress", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, progress.Count);
        Assert.All(progress, entry => Assert.Contains("[REDACTED]", entry.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("named-secret", StringComparison.Ordinal)
            || entry.Message.Contains("positional-secret", StringComparison.Ordinal));
        Assert.All(results, result => Assert.True(result.Success));
        if (quiet)
            Assert.All(results, result => Assert.NotNull(result.Data));
    }

    private sealed class ProgressLog : IShellLog
    {
        internal ConcurrentQueue<ShellLogEntry> Entries { get; } = new();
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _progress;
        public void Log(ShellLogLevel level, string category, string message)
        {
            Entries.Enqueue(new ShellLogEntry(DateTime.Now, level, category, message));
            if (category.StartsWith("cmd:progress", StringComparison.Ordinal) && Interlocked.Increment(ref _progress) == 4)
                Complete.TrySetResult();
        }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries.ToArray();
    }
}
