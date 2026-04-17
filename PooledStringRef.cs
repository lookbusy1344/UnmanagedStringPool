namespace LookBusy;

using System;

/// <summary>
/// Handle to a string allocated from a <see cref="SegmentedStringPool"/>. 16 bytes:
/// pool reference (8), slot index (4), generation (4). Value-equality via record struct.
/// <para>
/// <see cref="default(PooledStringRef)"/> is the empty sentinel; real allocations always have
/// generation ≥ 1. Disposing any copy invalidates all copies of the same allocation (generation
/// bump on free), matching the existing <see cref="PooledString"/> semantics.
/// </para>
/// </summary>
public readonly record struct PooledStringRef(
	SegmentedStringPool? Pool,
	uint SlotIndex,
	uint Generation
) : IDisposable
{
	public static PooledStringRef Empty => default;

	public bool IsEmpty => Pool is null && SlotIndex == 0u && Generation == 0u;

	public void Dispose() { /* filled in later tasks */ }

	public readonly bool Equals(PooledStringRef other) => false; // TODO
	public override readonly int GetHashCode() => 0; // TODO
}
