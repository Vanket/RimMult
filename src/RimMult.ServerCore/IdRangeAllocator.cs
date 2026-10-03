using System;

namespace RimMult.ServerCore;

public readonly struct IdRange
{
    public IdRange(long start, int count)
    {
        Start = start;
        Count = count;
    }

    public long Start { get; }
    public int Count { get; }
    public long End => Start + Count;

    public bool Contains(long id) => id >= Start && id < End;
}

/// <summary>
/// Hands out disjoint blocks of unique ids. RimWorld numbers things, pawns, jobs and so on from per-game counters;
/// with several machines simulating parts of one world those counters would collide, so every simulation
/// authority leases its own block and only allocates from it.
/// </summary>
public sealed class IdRangeAllocator
{
    private long _next;

    /// <param name="firstId">
    /// Must be above every id that already exists in the world (the save's counters), so leased blocks never
    /// overlap ids created before multiplayer started.
    /// </param>
    public IdRangeAllocator(long firstId, int blockSize = 100_000)
    {
        if (firstId < 0)
            throw new ArgumentOutOfRangeException(nameof(firstId));
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _next = firstId;
        BlockSize = blockSize;
    }

    public int BlockSize { get; }

    /// <summary>First id that has never been leased; persisted with the world so leases survive restarts.</summary>
    public long NextUnleased => _next;

    public IdRange Lease()
    {
        // RimWorld stores most ids as int; running past that is a world that cannot be saved correctly anymore.
        if (_next + BlockSize > int.MaxValue)
            throw new InvalidOperationException("Unique id space exhausted");

        var range = new IdRange(_next, BlockSize);
        _next += BlockSize;
        return range;
    }
}
