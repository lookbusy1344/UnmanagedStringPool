namespace LookBusy;

using System;
using System.Runtime.CompilerServices;

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

public sealed record SegmentedStringPoolOptions(
	int InitialSlotCapacity = SegmentedConstants.DefaultSlotCapacity,
	int SlabCellsPerSlab = SegmentedConstants.DefaultSlabCellsPerSlab,
	int ArenaSegmentBytes = SegmentedConstants.DefaultArenaSegmentBytes,
	int SmallStringThresholdChars = SegmentedConstants.DefaultSmallStringThresholdChars
);

public sealed class SegmentedStringPool : IDisposable
{
	private readonly SegmentedSlotTable slots;
	private readonly SegmentedSlabTier slabTier;
	private readonly SegmentedArenaTier arenaTier;
	private readonly int smallThreshold;
	private bool disposed;

	public SegmentedStringPool() : this(new SegmentedStringPoolOptions()) { }

	public SegmentedStringPool(SegmentedStringPoolOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		slots = new SegmentedSlotTable(options.InitialSlotCapacity);
		slabTier = new SegmentedSlabTier(options.SlabCellsPerSlab);
		arenaTier = new SegmentedArenaTier(options.ArenaSegmentBytes);
		smallThreshold = options.SmallStringThresholdChars;
	}

	public int ActiveAllocations => slots.ActiveCount;
	public long TotalBytesUnmanaged => slabTier.UnmanagedBytes + arenaTier.UnmanagedBytes;
	public long TotalBytesManaged => slots.Capacity * 16L;
	public int SlabCount => slabTier.SlabCount;
	public int SegmentCount => arenaTier.SegmentCount;
	internal bool IsDisposed => disposed;

	public PooledStringRef Allocate(ReadOnlySpan<char> value)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		if (value.IsEmpty) {
			return PooledStringRef.Empty;
		}
		var length = value.Length;
		var ptr = AllocateUnmanaged(length, out var tier);
		unsafe {
			fixed (char* src = value) {
				Buffer.MemoryCopy(src, (void*)ptr, length * sizeof(char), length * sizeof(char));
			}
		}
		var taggedPtr = new IntPtr((ptr.ToInt64() & SegmentedConstants.PtrMask) | (long)tier);
		var (slotIndex, gen) = slots.Allocate(taggedPtr, length);
		return new PooledStringRef(this, slotIndex, gen);
	}

	internal ReadOnlySpan<char> ReadSlot(uint slotIndex, uint generation)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
			throw new InvalidOperationException("PooledStringRef is stale or freed");
		}
		var raw = new IntPtr(entry.Ptr.ToInt64() & SegmentedConstants.PtrMask);
		unsafe {
			return new ReadOnlySpan<char>((void*)raw, entry.LengthChars);
		}
	}

	internal int GetLength(uint slotIndex, uint generation)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
			throw new InvalidOperationException("PooledStringRef is stale or freed");
		}
		return entry.LengthChars;
	}

	internal void FreeSlot(uint slotIndex, uint generation)
	{
		if (disposed) { return; }
		if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
			return;
		}
		var raw = new IntPtr(entry.Ptr.ToInt64() & SegmentedConstants.PtrMask);
		var tier = (int)(entry.Ptr.ToInt64() & SegmentedConstants.TierTagMask);
		if (tier == SegmentedConstants.TierSlab) {
			var slab = slabTier.LocateSlabByPointer(raw);
			slabTier.Free(raw, slab);
		} else {
			var seg = arenaTier.LocateSegmentByPointer(raw);
			SegmentedArenaTier.Free(raw, entry.LengthChars * sizeof(char), seg);
		}
		_ = slots.Free(slotIndex, generation);
	}

	public void Clear()
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		slots.ClearAllSlots();
		slabTier.ResetAll();
		arenaTier.ResetAll();
	}

	public void Reserve(int chars)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		if (chars <= 0) {
			return;
		}
		var smallBudget = chars / 2;
		var largeBudget = chars - smallBudget;
		slabTier.Reserve(smallBudget);
		arenaTier.Reserve(largeBudget * sizeof(char));
	}

	public void Dispose()
	{
		if (!disposed) {
			disposed = true;
			slots.ClearAllSlots();
			slabTier.Dispose();
			arenaTier.Dispose();
		}
		GC.SuppressFinalize(this);
	}

	~SegmentedStringPool() => Dispose();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private IntPtr AllocateUnmanaged(int charCount, out int tier)
	{
		if (charCount <= smallThreshold) {
			tier = SegmentedConstants.TierSlab;
			return slabTier.Allocate(charCount, out _);
		}
		tier = SegmentedConstants.TierArena;
		var byteCount = charCount * sizeof(char);
		return arenaTier.Allocate(byteCount, out _);
	}
}
