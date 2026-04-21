namespace LookBusy;

using System;

/// <summary>
/// Per-allocation slot metadata. 16 bytes aligned to 8. High bit of Generation marks slot as free;
/// when free, Ptr stores the next-free-slot index cast to IntPtr, and LengthChars is unused.
/// </summary>
internal struct SegmentedSlotEntry
{
	// When live: tagged unmanaged pointer (bit 0 = tier, bits 3..63 = address).
	// When freed: next-free-slot index stored as IntPtr — the Ptr field serves double duty.
	public IntPtr Ptr;

	public int LengthChars;

	// Layout: [bit31=freeFlag | bits30..0=reuse counter]. Counter always increments on every state change,
	// so a PooledStringRef's captured generation is invalidated immediately on free, even if the slot is reused.
	public uint Generation;

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
