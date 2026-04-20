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
	public readonly int CellBytes;
	public readonly int CellCount;
	public readonly IntPtr Buffer;
	private readonly ulong[] bitmap;
	private int freeCells;
	private bool disposed;

	public SegmentedSlab(int cellBytes, int cellCount)
	{
		if (cellBytes < SegmentedConstants.PtrAlignment) {
			throw new ArgumentOutOfRangeException(nameof(cellBytes));
		}
		if (cellCount < 1 || cellCount > 65536) {
			throw new ArgumentOutOfRangeException(nameof(cellCount));
		}
		CellBytes = cellBytes;
		CellCount = cellCount;
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
		freeCells = cellCount;
	}

	public int FreeCells => freeCells;

	public bool IsFull => freeCells == 0;

	public SegmentedSlab? NextInClass { get; set; }

	/// <summary>
	/// Allocates the first free cell using <see cref="BitOperations.TrailingZeroCount"/> to find the lowest set bit
	/// (set = free under the 1=free convention). Returns false if the slab is full.
	/// </summary>
	public bool TryAllocateCell(out int cellIndex)
	{
		for (var w = 0; w < bitmap.Length; w++) {
			var word = bitmap[w];
			if (word != 0UL) {
				// tzcnt finds the lowest set (free) bit; on x86 this is a single instruction.
				var bit = BitOperations.TrailingZeroCount(word);
				cellIndex = (w * 64) + bit;
				if (cellIndex >= CellCount) {
					break;
				}
				bitmap[w] = word & ~(1UL << bit); // flip free→used
				--freeCells;
				return true;
			}
		}
		cellIndex = -1;
		return false;
	}

	/// <summary>Marks a cell free by setting its bitmap bit. Throws if the cell is already free (double-free guard).</summary>
	public void FreeCell(int cellIndex)
	{
		if ((uint)cellIndex >= (uint)CellCount) {
			throw new ArgumentOutOfRangeException(nameof(cellIndex));
		}
		var w = cellIndex / 64;
		var bit = cellIndex & 63;
		var mask = 1UL << bit;
		if ((bitmap[w] & mask) != 0UL) {
			throw new InvalidOperationException("Cell already free");
		}
		bitmap[w] |= mask;
		++freeCells;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int OffsetOfCell(int cellIndex) => cellIndex * CellBytes;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CellIndexFromOffset(int offsetBytes) => offsetBytes / CellBytes;

	public bool Contains(IntPtr ptr)
	{
		var raw = ptr.ToInt64();
		var start = Buffer.ToInt64();
		var end = start + ((long)CellBytes * CellCount);
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
		var excess = (bitmap.Length * 64) - CellCount;
		if (excess > 0) {
			bitmap[^1] &= (1UL << (64 - excess)) - 1UL;
		}
		freeCells = CellCount;
	}

	public long UnmanagedBytes => (long)CellBytes * CellCount;

	public void Dispose()
	{
		if (!disposed) {
			Marshal.FreeHGlobal(Buffer);
			disposed = true;
		}
	}
}
