namespace LookBusy;

using System;

/// <summary>
/// Per-allocation slot metadata. 32 bytes aligned to 8. High bit of Generation marks slot as free;
/// when free, Ptr stores the next-free-slot index cast to IntPtr, LengthChars and AllocatedBytes are
/// zeroed, and Owner is nulled so the slab/segment is not kept alive by the slot table.
/// </summary>
internal struct SegmentedSlotEntry
{
	// When live: tagged unmanaged pointer (bit 0 = tier, bits 3..63 = address).
	// When freed: next-free-slot index stored as IntPtr — the Ptr field serves double duty.
	public IntPtr Ptr;

	// Owning SegmentedSlab (slab tier) or SegmentedArenaSegment (arena tier). Nulled on free
	// to avoid rooting the owner through the slot table after deallocation.
	public object? Owner;

	public int LengthChars;

	// Actual bytes handed out by TryAllocate, including any sub-MinArenaBlockBytes remainder
	// that was not split off. Used by FreeSlot for arena tier so the full block is returned.
	// Zero for slab-tier slots (cell size is derived from the owning slab's size class).
	public int AllocatedBytes;

	// Layout: [bit31=freeFlag | bits30..0=reuse counter]. Counter always increments on every state change,
	// so a PooledStringRef's captured generation is invalidated immediately on free, even if the slot is reused.
	public uint Generation;

	// 4 bytes implicit padding to reach 32-byte size.

	public static bool IsFree(uint generation) => (generation & SegmentedConstants.HighBit) != 0u;

	// ReSharper disable once MemberCanBePrivate.Global
	public static uint GenerationValue(uint generation) => generation & SegmentedConstants.GenerationMask;

	// Counter bumps on free so a stale ref is rejected even before the slot is reused.
	public static uint MarkFreeAndBumpGen(uint generation) =>
		((GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask) | SegmentedConstants.HighBit;

	// Counter bumps on re-allocation so every live ref holds a distinct generation across its full lifetime.
	public static uint ClearFreeAndBumpGen(uint generation) =>
		(GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask;
}
