namespace ValleytalkReborn
{
    internal sealed class GotoTimeoutCounter
    {
        private readonly int _initial;
        private int _remaining;

        public GotoTimeoutCounter(int initialTicks)
        {
            _initial   = initialTicks;
            _remaining = initialTicks;
        }

        /// <returns>true when the counter has reached zero (timeout).</returns>
        public bool Tick() => --_remaining <= 0;

        public void Reset() => _remaining = _initial;
    }
}
