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
// Registration is memory-only and process-level idempotent (static _installed).

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;

internal static class TestEnvironment
{
    private static bool _installed;

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
