namespace LookBusy;

/// <summary>
/// Constants shared across SegmentedStringPool internal types.
/// </summary>
internal static class SegmentedConstants
{
	public const uint HighBit = 0x80000000u;
	public const uint GenerationMask = 0x7FFFFFFFu;
	public const uint NoFreeSlot = 0xFFFFFFFFu;
	public const int PtrAlignment = 8;
	public const long TierTagMask = 1L;
	public const long PtrMask = ~7L;
	public const int TierSlab = 0;
	public const int TierArena = 1;
	public const int SlabSizeClassCount = 5;
	public const int MinArenaBlockBytes = 16;
	public const int ArenaBinCount = 16;
	public const int DefaultSlotCapacity = 64;
	public const int DefaultSlabCellsPerSlab = 256;
	public const int DefaultArenaSegmentBytes = 1 << 20;
	public const int DefaultSmallStringThresholdChars = 128;
}

/// <summary>
/// Segmented unmanaged string pool. Full implementation added in later tasks.
/// </summary>
public sealed class SegmentedStringPool
{
	// Implementation added in Task 6 and beyond.
}
