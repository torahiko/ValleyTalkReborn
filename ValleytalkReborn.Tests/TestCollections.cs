// TestCollections.cs
// xUnit collection definitions for ValleytalkReborn.Tests.
// Tests that mutate ModEntry static state MUST be placed in a dedicated,
// non-parallel collection to prevent cross-test contamination.

using Xunit;

// ─────────────────────────────────────────────────────────────────────────────
// CTX-011: Context.IsWorldReady is a process-level static, and its only injection
// point (a non-public setter, see TestEnvironment.WithWorldReady) mutates state
// visible to every class in the process. A [CollectionDefinition] with
// DisableParallelization only serialises *within* that collection — it cannot
// exclude classes that are not its members, and the classes asserting the
// "no world" state (ContextRouterBaselineTests anchor B / movement-baseline
// default flags) are outside CTX-011's allowed_files and cannot be marked.
// The assembly-level switch below is therefore the only mechanism available
// inside the allowed file set that actually guarantees mutual exclusion.
// ─────────────────────────────────────────────────────────────────────────────
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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

/// <summary>
/// CTX-011: bucket for test classes that flip Context.IsWorldReady via
/// TestEnvironment.WithWorldReady. Kept separate from StaticGlobalStateCollection
/// so the world-ready surface stays identifiable; combined with the assembly-level
/// DisableTestParallelization above, its members never overlap in time with the
/// classes that assert the "no world" state.
/// </summary>
[CollectionDefinition("WorldReadyStateCollection", DisableParallelization = true)]
public class WorldReadyStateCollection
{
    // Marker class only — xUnit uses the attribute to bucket test classes.
}
