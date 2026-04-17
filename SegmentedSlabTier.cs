namespace LookBusy;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Manages slab chains per size class (8, 16, 32, 64, 128 chars). Maintains an active slab per
/// class (first non-full); keeps full slabs on the chain for address-based lookup during free.
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
		var slab = activeSlabs[sizeClass];
		if (slab is null || slab.IsFull) {
			slab = FindNonFullSlabInClass(sizeClass) ?? AllocateNewSlab(sizeClass);
			activeSlabs[sizeClass] = slab;
		}
		if (!slab.TryAllocateCell(out var cellIndex)) {
			slab = AllocateNewSlab(sizeClass);
			activeSlabs[sizeClass] = slab;
			_ = slab.TryAllocateCell(out cellIndex);
		}
		owningSlab = slab;
		return new IntPtr(slab.Buffer.ToInt64() + slab.OffsetOfCell(cellIndex));
	}

	public static void Free(IntPtr ptr, SegmentedSlab slab)
	{
		var offset = (int)(ptr.ToInt64() - slab.Buffer.ToInt64());
		slab.FreeCell(slab.CellIndexFromOffset(offset));
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
		foreach (var s in allSlabs) {
			s.ResetAllCellsFree();
		}
		for (var i = 0; i < activeSlabs.Length; i++) {
			activeSlabs[i] = null;
			foreach (var s in allSlabs) {
				if (s.CellBytes == CellBytesForSizeClass(i)) {
					activeSlabs[i] = s;
					break;
				}
			}
		}
	}

	public void Reserve(int smallChars)
	{
		var perSlabChars = cellsPerSlab * (CellBytesForSizeClass(SegmentedConstants.SlabSizeClassCount - 1) / sizeof(char));
		while (allSlabs.Count * perSlabChars < smallChars) {
			_ = AllocateNewSlab(SegmentedConstants.SlabSizeClassCount - 1);
		}
	}

	public void Dispose()
	{
		foreach (var s in allSlabs) {
			s.Dispose();
		}
		allSlabs.Clear();
	}

	private SegmentedSlab AllocateNewSlab(int sizeClass)
	{
		var slab = new SegmentedSlab(CellBytesForSizeClass(sizeClass), cellsPerSlab);
		allSlabs.Add(slab);
		return slab;
	}

	private SegmentedSlab? FindNonFullSlabInClass(int sizeClass)
	{
		var cellBytes = CellBytesForSizeClass(sizeClass);
		foreach (var s in allSlabs) {
			if (s.CellBytes == cellBytes && !s.IsFull) {
				return s;
			}
		}
		return null;
	}
}
