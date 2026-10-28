using System.Runtime.CompilerServices;
using Strata.Core;

// Tests share static state (storage root, clock, weather cache); run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Strata.Tests;

internal static class TestSetup
{
    public static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "strata-tests-" + Environment.ProcessId);

    /// <summary>Before any test runs, point storage at a throwaway folder so tests never touch real user data.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(DataRoot);
        Storage.Configure(DataRoot);
    }
}
