namespace LookBusy;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// A single slab of fixed-size cells backed by unmanaged memory. Cells are tracked via a bitmap
/// (1 = free, 0 = used). Allocation uses <see cref="BitOperations.TrailingZeroCount(ulong)"/> to
/// locate the first free cell in O(1).
/// </summary>
internal sealed class SegmentedSlab : IDisposable
{
	public readonly int SizeClass;
	public readonly IntPtr Buffer;
	private readonly int cellBytes;
	private readonly int cellCount;
	private readonly ulong[] bitmap;
	private bool disposed;

	public SegmentedSlab(int sizeClass, int cellBytes, int cellCount)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(cellBytes, SegmentedConstants.PtrAlignment);
		if (cellCount is < 1 or > 65536) {
			throw new ArgumentOutOfRangeException(nameof(cellCount));
		}

		SizeClass = sizeClass;
		this.cellBytes = cellBytes;
		this.cellCount = cellCount;
		Buffer = Marshal.AllocHGlobal(cellBytes * cellCount);
		var words = (cellCount + 63) / 64;
		bitmap = new ulong[words];
		// Convention: 1=free, 0=used. Start fully free.
		for (var w = 0; w < words; w++) {
			bitmap[w] = ulong.MaxValue;
		}

		// Phantom bits past CellCount in the last word must be cleared so tzcnt never picks a non-existent cell.
		var excess = (words * 64) - cellCount;
		if (excess > 0) {
			bitmap[^1] &= (1UL << (64 - excess)) - 1UL;
		}

		FreeCells = cellCount;
	}

	public int FreeCells { get; private set; }

	public bool IsFull => FreeCells == 0;

	public SegmentedSlab? NextInClass { get; set; }

	/// <summary>
	/// Allocates the first free cell using <see cref="BitOperations.TrailingZeroCount"/> to find the lowest set bit
	/// (set = free under the 1=free convention). Returns false if the slab is full.
	/// </summary>
	public bool TryAllocateCell(out int cellIndex)
	{
		for (var w = 0; w < bitmap.Length; w++) {
			var word = bitmap[w];
			if (word == 0UL) {
				continue;
			}

			// tzcnt finds the lowest set (free) bit; on x86 this is a single instruction.
			var bit = BitOperations.TrailingZeroCount(word);
			cellIndex = (w * 64) + bit;
			if (cellIndex >= cellCount) {
				break;
			}

			bitmap[w] = word & ~(1UL << bit); // flip free→used
			--FreeCells;
			return true;
		}

		cellIndex = -1;
		return false;
	}

	/// <summary>Marks a cell free by setting its bitmap bit. Throws if the cell is already free (double-free guard).</summary>
	public void FreeCell(int cellIndex)
	{
		if ((uint)cellIndex >= (uint)cellCount) {
			throw new ArgumentOutOfRangeException(nameof(cellIndex));
		}

		var w = cellIndex / 64;
		var bit = cellIndex & 63;
		var mask = 1UL << bit;
		if ((bitmap[w] & mask) != 0UL) {
			throw new InvalidOperationException("Cell already free");
		}

		bitmap[w] |= mask;
		++FreeCells;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int OffsetOfCell(int cellIndex) => cellIndex * cellBytes;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CellIndexFromOffset(int offsetBytes) => offsetBytes / cellBytes;

	public bool Contains(IntPtr ptr)
	{
		var raw = ptr.ToInt64();
		var start = Buffer.ToInt64();
		var end = start + ((long)cellBytes * cellCount);
		return raw >= start && raw < end;
	}

	/// <summary>
	/// Resets the bitmap to all-free without freeing the unmanaged buffer. Used by <c>pool.Clear()</c> to
	/// reclaim cell space while reusing the already-allocated slab memory.
	/// </summary>
	public void ResetAllCellsFree()
	{
		for (var w = 0; w < bitmap.Length; w++) {
			bitmap[w] = ulong.MaxValue;
		}

		var excess = (bitmap.Length * 64) - cellCount;
		if (excess > 0) {
			bitmap[^1] &= (1UL << (64 - excess)) - 1UL;
		}

		FreeCells = cellCount;
	}

	public long UnmanagedBytes => (long)cellBytes * cellCount;

	public void Dispose()
	{
		if (disposed) {
			return;
		}

		Marshal.FreeHGlobal(Buffer);
		disposed = true;
	}
}
