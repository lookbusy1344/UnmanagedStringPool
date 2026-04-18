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

	private readonly SegmentedSlab?[] activeSlabs = new SegmentedSlab?[SegmentedConstants.SlabSizeClassCount];
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

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int ChooseSizeClass(int charCount)
	{
		if (charCount <= 8) { return 0; }
		if (charCount <= 16) { return 1; }
		if (charCount <= 32) { return 2; }
		if (charCount <= 64) { return 3; }
		if (charCount <= 128) { return 4; }
		return -1;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int CellBytesForSizeClass(int sizeClass) => SizeClassChars[sizeClass] * sizeof(char);

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
			DetachHead(sizeClass);
		}
		owningSlab = slab;
		return new IntPtr(slab.Buffer.ToInt64() + slab.OffsetOfCell(cellIndex));
	}

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

	public void ResetAll()
	{
		for (var i = 0; i < activeSlabs.Length; i++) {
			activeSlabs[i] = null;
		}
		foreach (var s in allSlabs) {
			s.ResetAllCellsFree();
			s.NextInClass = null;
		}
		// Re-thread every slab into its size-class chain.
		foreach (var s in allSlabs) {
			LinkAtHead(SizeClassForCellBytes(s.CellBytes), s);
		}
	}

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
		for (var i = 0; i < activeSlabs.Length; i++) {
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
