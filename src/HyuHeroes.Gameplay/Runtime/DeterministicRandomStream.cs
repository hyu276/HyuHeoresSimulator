/**
 * DETERMINISTIC_RANDOM_STREAM
 * Purpose: Provides a runtime-stable deterministic random stream whose complete mutable state is a single snapshot-owned UInt64.
 * Connections: MatchStateSnapshot persists the state while GameplayRuntimeContext exposes the stream to RANDOM_N target selection.
 * Risk: High because algorithm or consumption-order changes alter replay-visible outcomes for every randomized gameplay primitive.
 */
using System;

namespace HyuHeroes.Gameplay.Runtime;

public sealed class DeterministicRandomStream : IDeterministicRandomSource
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;
    private const ulong Mix1 = 0xBF58476D1CE4E5B9UL;
    private const ulong Mix2 = 0x94D049BB133111EBUL;

    public DeterministicRandomStream(ulong state)
    {
        State = state;
    }

    public ulong State { get; private set; }

    public int NextInt(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum), "Exclusive maximum must be positive.");
        }

        var bound = (uint)exclusiveMaximum;
        var rejectionThreshold = unchecked(0u - bound) % bound;
        while (true)
        {
            var candidate = (uint)(NextUInt64() >> 32);
            if (candidate >= rejectionThreshold)
            {
                return (int)(candidate % bound);
            }
        }
    }

    private ulong NextUInt64()
    {
        State = unchecked(State + Gamma);
        var value = State;
        value = unchecked((value ^ (value >> 30)) * Mix1);
        value = unchecked((value ^ (value >> 27)) * Mix2);
        return value ^ (value >> 31);
    }
}
