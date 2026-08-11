using System.Threading;

namespace LethalAICrewmate
{
    /// <summary>
    /// Atomically decides whether a deferred Unity-thread action starts or is cancelled first.
    /// A caller may safely report a timeout only when cancellation wins this race; once execution
    /// starts, the caller must wait for the real result instead of claiming that nothing happened.
    /// </summary>
    internal sealed class DeferredActionGate
    {
        private const int Pending = 0;
        private const int Started = 1;
        private const int Cancelled = 2;
        private int _state = Pending;

        internal bool TryBegin() =>
            Interlocked.CompareExchange(ref _state, Started, Pending) == Pending;

        internal bool TryCancel() =>
            Interlocked.CompareExchange(ref _state, Cancelled, Pending) == Pending;
    }
}
