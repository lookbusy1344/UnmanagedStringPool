namespace LookBusy;

using System;
using System.Runtime.CompilerServices;

internal static class SegmentedConstants
{
	public const uint HighBit = 0x80000000u; // bit 31 of Generation: set=freed, clear=live
	public const uint GenerationMask = 0x7FFFFFFFu; // lower 31 bits: monotonically-increasing reuse counter
	public const uint NoFreeSlot = 0xFFFFFFFFu; // sentinel stored in freeHead when the slot free chain is empty
	public const int PtrAlignment = 8; // Marshal.AllocHGlobal guarantees ≥8-byte alignment, so low 3 bits of every pointer are always zero
	public const long TierTagMask = 1L; // bit 0 of a tagged pointer: 0=slab tier, 1=arena tier
	public const long PtrMask = ~7L; // clears the 3 tag bits from a tagged pointer to recover the raw address
	public const int TierSlab = 0; // bit 0 value: allocation lives in a slab cell
	public const int TierArena = 1; // bit 0 value: allocation lives in an arena block
	public const int SlabSizeClassCount = 5; // five classes: 8/16/32/64/128 chars; doubling progression caps internal waste at <50% per live string
	public const int MinArenaBlockBytes = 16; // a free block must hold a SegmentedFreeBlockHeader (16 bytes) in its own payload
	public const int ArenaBinCount = 16; // bins 0..15 cover Log2(size)-4, so bin 0 = 16 B, bin 15 ≥ 256 KB
	public const int DefaultSlotCapacity = 64;
	public const int DefaultSlabCellsPerSlab = 256;
	public const int DefaultArenaSegmentBytes = 1 << 20; // 1 MB: amortises Marshal.AllocHGlobal overhead over many large strings
	public const int DefaultSmallStringThresholdChars = 128; // strings ≤ threshold go to slab tier; above go to arena tier
}

public sealed record SegmentedStringPoolOptions(
	int InitialSlotCapacity = SegmentedConstants.DefaultSlotCapacity,
	int SlabCellsPerSlab = SegmentedConstants.DefaultSlabCellsPerSlab,
	int ArenaSegmentBytes = SegmentedConstants.DefaultArenaSegmentBytes,
	int SmallStringThresholdChars = SegmentedConstants.DefaultSmallStringThresholdChars
);

/// <summary>
/// A GC-pressure-free string pool backed by unmanaged memory. Strings shorter than
/// <see cref="SegmentedStringPoolOptions.SmallStringThresholdChars"/> are stored in the slab tier
/// (fixed-size cells, bitmap-tracked, O(1) alloc/free); longer strings go to the arena tier
/// (bump + segregated free lists). Callers hold <see cref="PooledStringRef"/> handles — 16-byte
/// value types that never pin or move any managed object.
/// </summary>
/// <remarks>
/// Not thread-safe. All callers sharing a pool instance must provide external synchronisation.
/// </remarks>
public sealed class SegmentedStringPool : IDisposable
{
	private readonly SegmentedSlotTable slots;
	private readonly SegmentedSlabTier slabTier;
	private readonly SegmentedArenaTier arenaTier;
	private readonly int smallThreshold;
	private bool disposed;

	public SegmentedStringPool() : this(new()) { }

	private SegmentedStringPool(SegmentedStringPoolOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		slots = new(options.InitialSlotCapacity);
		slabTier = new(options.SlabCellsPerSlab);
		arenaTier = new(options.ArenaSegmentBytes);
		smallThreshold = options.SmallStringThresholdChars;
	}

	public int ActiveAllocations => slots.ActiveCount;
	public long TotalBytesUnmanaged => slabTier.UnmanagedBytes + arenaTier.UnmanagedBytes;
	public long TotalBytesManaged => slots.Capacity * 16L;
	public int SlabCount => slabTier.SlabCount;
	public int SegmentCount => arenaTier.SegmentCount;
	internal bool IsDisposed => disposed;

	/// <summary>
	/// Copies <paramref name="value"/> into unmanaged memory and returns a <see cref="PooledStringRef"/> handle.
	/// Empty spans return <see cref="PooledStringRef.Empty"/> without touching either tier.
	/// </summary>
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

		// bit 0 of the raw pointer is guaranteed zero by 8-byte alignment, so OR-ing the tier tag is safe
		var taggedPtr = new IntPtr((ptr.ToInt64() & SegmentedConstants.PtrMask) | (uint)tier);
		var (slotIndex, gen) = slots.Allocate(taggedPtr, length);
		return new(this, slotIndex, gen);
	}

	/// <summary>
	/// Resolves a handle to the raw character span in unmanaged memory. The tier tag stored in bit 0 of the
	/// slot's pointer is masked off before building the span — reading never needs to know which tier owns the data.
	/// </summary>
	internal ReadOnlySpan<char> ReadSlot(uint slotIndex, uint generation)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
			throw new InvalidOperationException("PooledStringRef is stale or freed");
		}

		var raw = new IntPtr(entry.Ptr.ToInt64() & SegmentedConstants.PtrMask);
		unsafe {
			return new((void*)raw, entry.LengthChars);
		}
	}

	/// <summary>Returns the character count for a live handle without constructing a span.</summary>
	internal int GetLength(uint slotIndex, uint generation)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		return !slots.TryReadSlot(slotIndex, generation, out var entry)
			? throw new InvalidOperationException("PooledStringRef is stale or freed")
			: entry.LengthChars;
	}

	/// <summary>
	/// Decodes the tier tag from the slot's pointer and routes the deallocation to the correct tier,
	/// then frees the slot entry. Silently no-ops if the pool is already disposed or the handle is stale.
	/// </summary>
	internal void FreeSlot(uint slotIndex, uint generation)
	{
		if (disposed) { return; } // silent: guards against PooledStringRef.Dispose() racing pool.Dispose()

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

	/// <summary>
	/// Invalidates all live handles and reclaims all string storage without freeing any unmanaged memory.
	/// Slabs and segments are reset and reused for subsequent allocations — use this to efficiently
	/// process a new batch of strings without the cost of re-allocating buffers from the OS.
	/// </summary>
	public void Clear()
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		slots.ClearAllSlots();
		slabTier.ResetAll();
		arenaTier.ResetAll();
	}

	/// <summary>
	/// Pre-allocates unmanaged capacity for at least <paramref name="chars"/> characters, split evenly between tiers,
	/// to avoid latency spikes on first use. Does not affect existing allocations.
	/// </summary>
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
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (disposed) {
			return;
		}

		disposed = true;
		if (disposing) {
			slots.ClearAllSlots();
			slabTier.Dispose();
			arenaTier.Dispose();
		}
	}

	/// <summary>
	/// Routes an allocation to the slab tier (≤ threshold) or arena tier (> threshold)
	/// and returns the raw unmanaged pointer together with the tier tag to be embedded in the slot.
	/// </summary>
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

	~SegmentedStringPool() => Dispose(false);
}
