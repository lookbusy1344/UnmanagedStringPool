namespace LookBusy;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Manages slab chains per size class (8, 16, 32, 64, 128 chars). Each class has an intrusive
/// singly-linked chain of non-full slabs threaded through <see cref="SegmentedSlab.NextInClass"/>;
/// <see cref="activeSlabs"/> holds the chain head per class. Full slabs are unlinked; freeing a cell
/// in a full slab re-links it to the chain head. <see cref="allSlabs"/> tracks every slab (full or
/// not) for address-based pointer lookup.
/// </summary>
internal sealed class SegmentedSlabTier : IDisposable
{
	private static readonly int[] SizeClassChars = [8, 16, 32, 64, 128];

	[InlineArray(SegmentedConstants.SlabSizeClassCount)]
	private struct ActiveSlabArray { private SegmentedSlab? _e; }

	// Chain invariant: every slab in activeSlabs[] has at least one free cell.
	// Full slabs are detached from their chain on the cycle they fill; freeing a cell in a full slab re-links it.
	private ActiveSlabArray activeSlabs;

	// Tracks every slab regardless of chain state so LocateSlabByPointer can find full (off-chain) slabs during Free.
	private readonly List<SegmentedSlab> allSlabs = [];
	private readonly int cellsPerSlab;

	public SegmentedSlabTier(int cellsPerSlab)
	{
		if (cellsPerSlab < 1) {
			throw new ArgumentOutOfRangeException(nameof(cellsPerSlab));
		}

		this.cellsPerSlab = cellsPerSlab;
	}

	public int SlabCount => allSlabs.Count;

	public long UnmanagedBytes
	{
		get
		{
			long total = 0;
			foreach (var s in allSlabs) {
				total += s.UnmanagedBytes;
			}

			return total;
		}
	}

	/// <summary>
	/// Returns the index of the smallest size class that fits <paramref name="charCount"/> chars,
	/// or -1 if the count exceeds the slab tier threshold (caller must route to the arena tier).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int ChooseSizeClass(int charCount) =>
		charCount switch {
			<= 8 => 0,
			<= 16 => 1,
			<= 32 => 2,
			<= 64 => 3,
			<= 128 => 4,
			_ => -1
		};

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int CellBytesForSizeClass(int sizeClass) => SizeClassChars[sizeClass] * sizeof(char);

	/// <summary>
	/// Allocates one cell from the appropriate size-class chain. If the chain head fills after allocation
	/// it is detached so the chain invariant (every slab has ≥1 free cell) is maintained.
	/// </summary>
	public IntPtr Allocate(int charCount, out SegmentedSlab owningSlab)
	{
		var sizeClass = ChooseSizeClass(charCount);
		if (sizeClass < 0) {
			throw new InvalidOperationException("Size exceeds slab threshold; caller should route to arena");
		}

		var slab = activeSlabs[sizeClass] ?? AllocateNewSlab(sizeClass);
		if (!slab.TryAllocateCell(out var cellIndex)) {
			// Chain-head invariant violated; recover by allocating a fresh slab.
			slab = AllocateNewSlab(sizeClass);
			_ = slab.TryAllocateCell(out cellIndex);
		}

		if (slab.IsFull) {
			DetachHead(sizeClass); // remove from chain; still tracked in allSlabs for pointer lookup
		}

		owningSlab = slab;
		return new(slab.Buffer.ToInt64() + slab.OffsetOfCell(cellIndex));
	}

	/// <summary>
	/// Returns one cell to its slab. If the slab was full (and therefore off its chain),
	/// re-links it at the chain head so it becomes available for future allocations again.
	/// </summary>
	public void Free(IntPtr ptr, SegmentedSlab slab)
	{
		var wasFull = slab.IsFull;
		var offset = (int)(ptr.ToInt64() - slab.Buffer.ToInt64());
		slab.FreeCell(slab.CellIndexFromOffset(offset));
		if (wasFull) {
			LinkAtHead(SizeClassForCellBytes(slab.CellBytes), slab);
		}
	}

	public SegmentedSlab LocateSlabByPointer(IntPtr ptr)
	{
		foreach (var s in allSlabs) {
			if (s.Contains(ptr)) {
				return s;
			}
		}

		throw new InvalidOperationException("Pointer does not belong to any slab in this tier");
	}

	/// <summary>
	/// Resets all slabs to fully-free and re-threads them into their size-class chains without freeing unmanaged memory.
	/// Used by <c>pool.Clear()</c>. The two-pass approach (disconnect all, then re-link all) avoids stale NextInClass
	/// pointers left over from previous chain state.
	/// </summary>
	public void ResetAll()
	{
		for (var i = 0; i < SegmentedConstants.SlabSizeClassCount; i++) {
			activeSlabs[i] = null;
		}

		foreach (var s in allSlabs) {
			s.ResetAllCellsFree();
			s.NextInClass = null;
		}

		// Re-thread every slab into its size-class chain in allSlabs insertion order.
		// LinkAtHead prepends, so the last slab processed for a class ends up as the chain head.
		foreach (var s in allSlabs) {
			LinkAtHead(SizeClassForCellBytes(s.CellBytes), s);
		}
	}

	/// <summary>
	/// Pre-allocates slab capacity for at least <paramref name="smallChars"/> chars of small-string storage.
	/// Always uses the largest size class (128-char cells) so any future small string fits in the reserved slabs.
	/// </summary>
	public void Reserve(int smallChars)
	{
		var sizeClass = SegmentedConstants.SlabSizeClassCount - 1;
		var perSlabChars = cellsPerSlab * (CellBytesForSizeClass(sizeClass) / sizeof(char));
		while (allSlabs.Count * perSlabChars < smallChars) {
			_ = AllocateNewSlab(sizeClass);
		}
	}

	public void Dispose()
	{
		foreach (var s in allSlabs) {
			s.Dispose();
		}

		allSlabs.Clear();
		for (var i = 0; i < SegmentedConstants.SlabSizeClassCount; i++) {
			activeSlabs[i] = null;
		}
	}

	private SegmentedSlab AllocateNewSlab(int sizeClass)
	{
		var slab = new SegmentedSlab(CellBytesForSizeClass(sizeClass), cellsPerSlab);
		allSlabs.Add(slab);
		LinkAtHead(sizeClass, slab);
		return slab;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void LinkAtHead(int sizeClass, SegmentedSlab slab)
	{
		slab.NextInClass = activeSlabs[sizeClass];
		activeSlabs[sizeClass] = slab;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void DetachHead(int sizeClass)
	{
		var head = activeSlabs[sizeClass];
		if (head is null) { return; }

		activeSlabs[sizeClass] = head.NextInClass;
		head.NextInClass = null;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int SizeClassForCellBytes(int cellBytes)
	{
		for (var i = 0; i < SizeClassChars.Length; i++) {
			if (CellBytesForSizeClass(i) == cellBytes) { return i; }
		}

		throw new InvalidOperationException("Unknown cell size");
	}
}
