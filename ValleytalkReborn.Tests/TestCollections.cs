// TestCollections.cs
// xUnit collection definitions for ValleytalkReborn.Tests.
// Tests that mutate ModEntry static state MUST be placed in a dedicated,
// non-parallel collection to prevent cross-test contamination.

using Xunit;

namespace ValleytalkReborn.Tests;

/// <summary>
/// Disables parallel execution for all test classes that modify ModEntry
/// static fields (SHelper / SMonitor / Config / _locale / _localeCache),
/// preventing race conditions and state leakage into unrelated test classes
/// such as EmotionalStateResolverTests.
/// </summary>
[CollectionDefinition("StaticGlobalStateCollection", DisableParallelization = true)]
public class StaticGlobalStateCollection
{
    // Marker class only — xUnit uses the attribute to bucket test classes.
}
