using System.Collections.Concurrent;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.NodeFixture;

/// <summary>Repository-owned integration fixture. Contains no real user module or user data.</summary>
public sealed class FixtureInfo : BaseVariable.ModuleInfoBase
{
    public override Type MainClassType => typeof(Fixture);
}

public sealed class Fixture : IModuleContextAware, IDisposable
{
    private IDisposable? _frontend = null;
    private FixtureFrontend? _ui = null;
    public void Attach(IModuleContext context)
    {
        var domain = context.Environment.ModuleName.Replace("History", "", StringComparison.Ordinal).ToLowerInvariant();
#if FRONTEND_FIXTURE
        _ui = new FixtureFrontend(context.Environment.DataDirectory);
        _frontend = context.RegisterFrontend(_ui);
#endif
        context.RegisterCommands(registry =>
        {
            registry.Register(new CommandDescriptor
            {
                Name = domain + ".ping",
                Summary = "Reports execution machine and fixture identity",
                Readonly = true,
                RequiresUiThread = _ui != null,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong", new
                {
                    machine = Environment.MachineName,
                    processId = Environment.ProcessId,
                    module = context.Environment.ModuleName,
                    threadId = Environment.CurrentManagedThreadId,
                    uiThreadId = _ui?.ThreadId,
                })),
            });
            registry.Register(new CommandDescriptor
            {
                Name = domain + ".ask",
                Summary = "Fixture confirmation probe",
                Level = CommandLevel.Ask,
                RequiresUiThread = _ui != null,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(context.Bus.RequestConfirmation("fixture nested confirmation") ? "approved" : "denied")),
            });
            registry.Register(new CommandDescriptor
            {
                Name = domain + ".write",
                Summary = "Appends one isolated marker after an optional delay",
                Parameters = [new ParameterSpec { Name = "delay", Description = "Delay milliseconds" }],
                Handler = async call =>
                {
                    await Task.Delay(call.GetInt("delay"), call.Cancellation);
                    File.AppendAllText(Path.Combine(context.Environment.DataDirectory, "writes.txt"), "write\n");
                    context.Publish(domain + ".changed", new { machine = Environment.MachineName });
                    return CommandResult.Ok("written");
                },
            });
            registry.Register(new CommandDescriptor
            {
                Name = domain + ".call",
                Summary = "Makes a nested call through the module bus",
                Parameters = [new ParameterSpec { Name = "command", Description = "Nested command", Required = true }],
                Handler = call => context.Bus.InvokeAsync(call.RequireString("command"), "fixture", call.Cancellation),
            });
            registry.Register(new CommandDescriptor
            {
                Name = domain + ".watch",
                Summary = "Records fixture events into isolated module data",
                Parameters = [new ParameterSpec { Name = "topic", Description = "Event topic", Required = true }],
                Handler = CommandDescriptor.Sync(call =>
                {
                    context.Subscribe(call.RequireString("topic"), evt =>
                        File.AppendAllText(Path.Combine(context.Environment.DataDirectory, "events.txt"), evt.Source + "\n"));
                    return CommandResult.Ok("subscribed");
                }),
            });
        });
    }
    public void Dispose() { _frontend?.Dispose(); _ui?.Dispose(); }

    private sealed class FixtureFrontend : SynchronizationContext, IFrontend, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly string _data;
        internal int ThreadId => _thread.ManagedThreadId;
        public SynchronizationContext UiContext => this;
        internal FixtureFrontend(string data)
        {
            _data = data;
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            })
            { IsBackground = true, Name = "Node fixture UI" };
            _thread.Start();
        }
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public bool Confirm(string prompt)
        {
            File.AppendAllText(Path.Combine(_data, "confirmations.txt"), Environment.MachineName + "\n");
            return true;
        }
        public Task<CommandResult> ExecuteAsync(string commandName, string source, CancellationToken cancellation)
            => Task.FromResult(CommandResult.Ok("fixture UI"));
        public void Dispose() { _queue.CompleteAdding(); if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2)); }
    }
}
