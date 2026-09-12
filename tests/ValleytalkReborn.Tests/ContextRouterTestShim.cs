namespace ValleyTalk.Tests
{
    internal static class ContextRouterTestShim
    {
        internal static bool TryDetectGotoIntent(string input, out string text)
            => ContextRouter.TryDetectGotoIntentInternal(input, out text);
    }
}
