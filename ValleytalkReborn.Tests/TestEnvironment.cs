// TestEnvironment.cs
// CTX-008: single-point headless SMAPI context shim shared by test fixtures.
//
// Wraps the two preconditions needed to drive SMAPI Context / game multiplayer
// checks outside a running game:
//   1) StardewModdingAPI.Context's static constructor depends on
//      SMAPI.Toolkit.dll (not copied to the test bin); register an
//      AssemblyResolve handler that loads it from the game's smapi-internal
//      directory.
//   2) Context.IsMultiplayer → LocalMultiplayer.IsLocalMultiplayer reads
//      GameRunner.instance.gameInstances.Count (NRE when instance is null);
//      inject an empty GameRunner (empty instance list) so the check
//      deterministically evaluates to "single-player".
//
// CTX-011: additionally exposes WithWorldReady(Action) — a scoped injection of
// Context.IsWorldReady that always restores the previous value in a finally.
// The setter is non-public (see precedent in CommunityChoreLedgerTests), so it is
// reached by reflection; this is the same mechanism already proven in-suite,
// promoted to shared fixture level.
//
// HAZARD: Context.IsWorldReady is a process-level static. Any test that flips it
// races with tests asserting the "no world" state (movement-baseline default
// flags, date-invitation anchor B). Callers MUST live in a non-parallel xUnit
// collection — see TestCollections.cs.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using StardewModdingAPI;
using StardewValley;

internal static class TestEnvironment
{
    private static bool _installed;

    /// <summary>
    /// Runs <paramref name="action"/> with Context.IsWorldReady forced to true,
    /// then restores the original value in a finally.
    /// </summary>
    internal static void WithWorldReady(Action action) => WithWorldReady(true, action);

    /// <summary>
    /// Counterpart of <see cref="WithWorldReady(Action)"/> that pins the flag to
    /// false. Exists so tests asserting the "no world" boundary state do not
    /// depend on ambient process state — Context.IsWorldReady is a process-level
    /// static, so any earlier fixture in the process may still leave it set
    /// (CTX-011 finding; CTX-011.5/CTX-013 moved every in-scope writer onto this
    /// scoped helper, which restores in a finally).
    /// </summary>
    internal static void WithoutWorldReady(Action action) => WithWorldReady(false, action);

    /// <summary>
    /// Scoped injection of Context.IsWorldReady. Memory-only, restores the value
    /// observed on entry in a finally, so reentrant use is safe. Reflection is
    /// required because the setter is non-public (precedent:
    /// CommunityChoreLedgerTests). No production code is touched.
    /// </summary>
    internal static void WithWorldReady(bool worldReady, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        // Guarantee the SMAPI Toolkit AssemblyResolve handler is registered
        // before anything touches Context (idempotent).
        InstallHeadlessContext();

        MethodInfo setter = typeof(Context)
            .GetProperty("IsWorldReady", BindingFlags.Public | BindingFlags.Static)!
            .GetSetMethod(nonPublic: true)!;

        bool original = Context.IsWorldReady;
        try
        {
            setter.Invoke(null, new object[] { worldReady });
            action();
        }
        finally
        {
            setter.Invoke(null, new object[] { original });
        }
    }

    internal static void InstallHeadlessContext()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new AssemblyName(args.Name).Name;
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
                "Stardew Valley", "smapi-internal", name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        var runner = FormatterServices.GetUninitializedObject(typeof(GameRunner));
        var instancesField = typeof(GameRunner).GetField("gameInstances",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        instancesField?.SetValue(runner, Activator.CreateInstance(instancesField.FieldType));
        typeof(GameRunner).GetField("instance",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.SetValue(null, runner);

        Game1.hasLocalClientsOnly = false;
    }
}
