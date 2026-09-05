using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>Serializes tests that claim process-wide host resources.</summary>
public static class TestCollections
{
    public const string HostProcess = "host-process";
}

[CollectionDefinition(TestCollections.HostProcess, DisableParallelization = true)]
public sealed class HostProcessCollection;
