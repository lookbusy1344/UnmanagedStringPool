namespace LookBusy;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Free-block header stored inline in the first 16 bytes of any freed arena block. Read/written
/// directly to unmanaged memory — never instantiated on the managed heap.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct SegmentedFreeBlockHeader
{
	public int SizeBytes;
	public int NextOffset;
	public int PrevOffset;
	public int BinIndex;
}

/// <summary>
/// Boundary-tag footer written at the last 8 bytes of every free block.
/// Enables O(1) backward coalescing: when freeing a block at offset N, read the
/// footer at N−8 to check if the preceding block is free and get its size.
/// In-use blocks do not carry a footer — the IsFree flag distinguishes stale data.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct SegmentedFreeBlockFooter
{
	public int SizeBytes; // mirrors header SizeBytes so we can locate the header
	public int IsFree;    // non-zero when the block is on the free list
}

/// <summary>
/// A single arena segment. Bump allocator from the tail + free list from the head, via segregated
/// bins keyed by Log2(blockSize). Free blocks embed their own link headers.
/// </summary>
internal sealed class SegmentedArenaSegment : IDisposable
{
	[InlineArray(SegmentedConstants.ArenaBinCount)]
	private struct BinHeadArray
	{
		private int _e;
	}

	public readonly IntPtr Buffer;
	public readonly int Capacity;
	public int BumpOffset;
	private BinHeadArray binHeads;
	private bool disposed;

	public SegmentedArenaSegment(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, SegmentedConstants.MinArenaBlockBytes);
		Capacity = capacity;
		Buffer = Marshal.AllocHGlobal(capacity);
		for (var i = 0; i < SegmentedConstants.ArenaBinCount; ++i) {
			binHeads[i] = -1;
		}
	}

	public long UnmanagedBytes => Capacity;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Contains(IntPtr ptr)
	{
		var raw = ptr.ToInt64();
		var start = Buffer.ToInt64();
		return raw >= start && raw < start + Capacity;
	}

	/// <summary>
	/// Attempts to allocate <paramref name="byteCount"/> bytes from this segment. Searches free-list bins from the
	/// smallest sufficient bin upward, splitting any oversized block. Falls back to bump allocation if no free block fits.
	/// Returns false if neither strategy can satisfy the request.
	/// <para>
	/// <paramref name="actualBytes"/> receives the true number of bytes handed out. This equals the aligned
	/// request size in the split and bump cases, but equals the full free-block size when the remainder after
	/// a split would be smaller than <see cref="SegmentedConstants.MinArenaBlockBytes"/> (no-split path).
	/// The caller must store this value and pass it back to <see cref="Free"/> to avoid orphaning the slack.
	/// </para>
	/// </summary>
	public bool TryAllocate(int byteCount, out IntPtr ptr, out int actualBytes)
	{
		var size = AlignSize(byteCount);
		var startBin = BinIndexForSize(size);
		for (var b = startBin; b < SegmentedConstants.ArenaBinCount; ++b) {
			var head = binHeads[b];
			while (head >= 0) {
				var hdr = ReadHeader(head);
				if (hdr.SizeBytes >= size) {
					UnlinkFromBin(ref hdr, head);
					var remainder = hdr.SizeBytes - size;
					if (remainder >= SegmentedConstants.MinArenaBlockBytes) {
						// Split the block: the tail portion becomes a new free block in its own bin.
						var tailOffset = head + size;
						WriteHeader(tailOffset,
							new() { SizeBytes = remainder, NextOffset = -1, PrevOffset = -1, BinIndex = BinIndexForSize(remainder) });
						LinkIntoBin(tailOffset);
						actualBytes = size;
					} else {
						// No-split: hand out the entire block. The remainder (< MinArenaBlockBytes)
						// is absorbed into the allocation; record the full block size so Free can
						// reclaim it correctly rather than orphaning the slack bytes.
						actualBytes = hdr.SizeBytes;
					}

					ptr = new(Buffer.ToInt64() + head);
					return true;
				}

				head = hdr.NextOffset;
			}
		}

		// Bump fallback: carve from the never-yet-used tail of the buffer.
		if (BumpOffset + size <= Capacity) {
			ptr = new(Buffer.ToInt64() + BumpOffset);
			BumpOffset += size;
			actualBytes = size;
			return true;
		}

		ptr = IntPtr.Zero;
		actualBytes = 0;
		return false;
	}

	/// <summary>
	/// Returns a block to the free list, writing a <see cref="SegmentedFreeBlockHeader"/> into the freed memory itself.
	/// Coalesces with adjacent free blocks before linking into a bin to reduce fragmentation.
	/// </summary>
	public void Free(IntPtr ptr, int byteCount)
	{
		var offset = (int)(ptr.ToInt64() - Buffer.ToInt64());
		var size = AlignSize(byteCount);
		TryCoalesceForward(ref offset, ref size);
		TryCoalesceBackward(ref offset, ref size);
		WriteHeader(offset, new() { SizeBytes = size, NextOffset = -1, PrevOffset = -1, BinIndex = BinIndexForSize(size) });
		LinkIntoBin(offset);
	}

	/// <summary>
	/// Resets the segment to its pristine state without freeing unmanaged memory. Used by <c>pool.Clear()</c>;
	/// setting <see cref="BumpOffset"/> to zero makes the entire buffer available to the bump allocator again.
	/// </summary>
	public void Reset()
	{
		BumpOffset = 0;
		for (var i = 0; i < SegmentedConstants.ArenaBinCount; ++i) {
			binHeads[i] = -1;
		}
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// Dispose pattern: disposing=true means Dispose() was called explicitly; disposing=false means finalizer is running.
	/// Currently only unmanaged resources (Buffer) need cleanup, so the parameter is unused. If managed resources
	/// (e.g., IDisposable fields) are added later, they should only be disposed when disposing=true.
	/// </summary>
	// ReSharper disable once UnusedParameter.Local
	private void Dispose(bool disposing)
	{
		if (!disposed) {
			Marshal.FreeHGlobal(Buffer);
			disposed = true;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int AlignSize(int size)
	{
		const int alignment = SegmentedConstants.PtrAlignment;
		return size < SegmentedConstants.MinArenaBlockBytes ? SegmentedConstants.MinArenaBlockBytes : (size + (alignment - 1)) & ~(alignment - 1);
	}

	// Maps block size to bin index via Log2(size) − 4.
	// The −4 normalises because the minimum block is 16 bytes and Log2(16) = 4,
	// so bin 0 covers [16, 32), bin 1 covers [32, 64), etc., up to bin 15 which is clamped for all larger blocks.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int BinIndexForSize(int size)
	{
		var log = BitOperations.Log2((uint)size);
		var bin = log - 4;
		if (bin < 0) { bin = 0; }

		if (bin >= SegmentedConstants.ArenaBinCount) { bin = SegmentedConstants.ArenaBinCount - 1; }

		return bin;
	}

	private unsafe SegmentedFreeBlockHeader ReadHeader(int offset) =>
		*(SegmentedFreeBlockHeader*)(Buffer.ToInt64() + offset);

	private unsafe void WriteHeader(int offset, SegmentedFreeBlockHeader header) =>
		*(SegmentedFreeBlockHeader*)(Buffer.ToInt64() + offset) = header;

	private unsafe SegmentedFreeBlockFooter ReadFooter(int offset) =>
		*(SegmentedFreeBlockFooter*)(Buffer.ToInt64() + offset);

	private unsafe void WriteFooter(int offset, SegmentedFreeBlockFooter footer) =>
		*(SegmentedFreeBlockFooter*)(Buffer.ToInt64() + offset) = footer;

	// Writes the boundary-tag footer at the end of a free block.
	// footerOffset = blockOffset + blockSize - sizeof(SegmentedFreeBlockFooter)
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MarkFooterFree(int blockOffset, int blockSize)
	{
		var footerOffset = blockOffset + blockSize - Unsafe.SizeOf<SegmentedFreeBlockFooter>();
		WriteFooter(footerOffset, new() { SizeBytes = blockSize, IsFree = 1 });
	}

	// Clears the free flag in the boundary-tag footer when a block is allocated.
	// Prevents stale footer data from being misread during coalescing of a neighbouring block.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MarkFooterInUse(int blockOffset, int blockSize)
	{
		var footerOffset = blockOffset + blockSize - Unsafe.SizeOf<SegmentedFreeBlockFooter>();
		WriteFooter(footerOffset, new() { SizeBytes = blockSize, IsFree = 0 });
	}

	private void LinkIntoBin(int offset)
	{
		var hdr = ReadHeader(offset);
		hdr.PrevOffset = -1;
		hdr.NextOffset = binHeads[hdr.BinIndex];
		WriteHeader(offset, hdr);
		if (hdr.NextOffset >= 0) {
			var next = ReadHeader(hdr.NextOffset);
			next.PrevOffset = offset;
			WriteHeader(hdr.NextOffset, next);
		}

		binHeads[hdr.BinIndex] = offset;
		MarkFooterFree(offset, hdr.SizeBytes);
	}

	private void UnlinkFromBin(ref SegmentedFreeBlockHeader hdr, int offset)
	{
		if (hdr.PrevOffset >= 0) {
			var prev = ReadHeader(hdr.PrevOffset);
			prev.NextOffset = hdr.NextOffset;
			WriteHeader(hdr.PrevOffset, prev);
		} else {
			binHeads[hdr.BinIndex] = hdr.NextOffset;
		}

		if (hdr.NextOffset >= 0) {
			var next = ReadHeader(hdr.NextOffset);
			next.PrevOffset = hdr.PrevOffset;
			WriteHeader(hdr.NextOffset, next);
		}

		MarkFooterInUse(offset, hdr.SizeBytes);
	}

	// O(1): the successor header is at a known offset; read it directly.
	// The BumpOffset guard prevents reads into uninitialised bump memory.
	private void TryCoalesceForward(ref int offset, ref int size)
	{
		var successorOffset = offset + size;
		if (successorOffset >= BumpOffset) {
			return;
		}

		var hdr = ReadHeader(successorOffset);
		if (hdr.SizeBytes > 0 && IsInBin(successorOffset, hdr.BinIndex)) {
			UnlinkFromBin(ref hdr, successorOffset);
			size += hdr.SizeBytes;
		}
	}

	// O(1): read the footer immediately before our block to find a free predecessor.
	private void TryCoalesceBackward(ref int offset, ref int size)
	{
		var footerSize = Unsafe.SizeOf<SegmentedFreeBlockFooter>();
		if (offset < footerSize) {
			return;
		}

		var footer = ReadFooter(offset - footerSize);
		if (footer.IsFree == 0 || footer.SizeBytes <= 0) {
			return;
		}

		var predOffset = offset - footer.SizeBytes;
		if (predOffset < 0) {
			return;
		}

		var hdr = ReadHeader(predOffset);
		// Confirm the header agrees — guards against stale footer data.
		if (hdr.SizeBytes != footer.SizeBytes || !IsInBin(predOffset, hdr.BinIndex)) {
			return;
		}

		UnlinkFromBin(ref hdr, predOffset);
		offset = predOffset;
		size += hdr.SizeBytes;
	}

	// Confirms a block at 'offset' is the current head or is reachable in the given bin.
	// Used as a consistency guard after reading boundary-tag data.
	private bool IsInBin(int offset, int binIndex)
	{
		if ((uint)binIndex >= (uint)SegmentedConstants.ArenaBinCount) {
			return false;
		}

		var cursor = binHeads[binIndex];
		while (cursor >= 0) {
			if (cursor == offset) {
				return true;
			}

			var hdr = ReadHeader(cursor);
			cursor = hdr.NextOffset;
		}

		return false;
	}

	~SegmentedArenaSegment() => Dispose(false);
}
