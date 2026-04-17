namespace LookBusy;

using System;

/// <summary>
/// Per-allocation slot metadata. 16 bytes aligned to 8. High bit of Generation marks slot as free;
/// when free, Ptr stores the next-free-slot index cast to IntPtr, and LengthChars is unused.
/// </summary>
internal struct SegmentedSlotEntry
{
	public IntPtr Ptr;
	public int LengthChars;
	public uint Generation;

	public static bool IsFree(uint generation) => (generation & SegmentedConstants.HighBit) != 0u;

	public static uint GenerationValue(uint generation) => generation & SegmentedConstants.GenerationMask;

	public static uint MarkFreeAndBumpGen(uint generation) =>
		((GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask) | SegmentedConstants.HighBit;

	public static uint ClearFreeAndBumpGen(uint generation) =>
		(GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask;
}
