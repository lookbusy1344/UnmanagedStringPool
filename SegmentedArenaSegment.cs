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
/// A single arena segment. Bump allocator from the tail + free list from the head, via segregated
/// bins keyed by Log2(blockSize). Free blocks embed their own link headers.
/// </summary>
internal sealed class SegmentedArenaSegment : IDisposable
{
	public readonly IntPtr Buffer;
	public readonly int Capacity;
	public int BumpOffset;
	private readonly int[] binHeads;
	private bool disposed;

	public SegmentedArenaSegment(int capacity)
	{
		if (capacity < SegmentedConstants.MinArenaBlockBytes) {
			throw new ArgumentOutOfRangeException(nameof(capacity));
		}
		Capacity = capacity;
		Buffer = Marshal.AllocHGlobal(capacity);
		binHeads = new int[SegmentedConstants.ArenaBinCount];
		for (var i = 0; i < binHeads.Length; i++) {
			binHeads[i] = -1;
		}
	}

	public long UnmanagedBytes => Capacity;

	public SegmentedArenaSegment? Next { get; set; }

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Contains(IntPtr ptr)
	{
		var raw = ptr.ToInt64();
		var start = Buffer.ToInt64();
		return raw >= start && raw < start + Capacity;
	}

	public bool TryAllocate(int byteCount, out IntPtr ptr)
	{
		var size = AlignSize(byteCount);
		var startBin = BinIndexForSize(size);
		for (var b = startBin; b < binHeads.Length; b++) {
			var head = binHeads[b];
			while (head >= 0) {
				var hdr = ReadHeader(head);
				if (hdr.SizeBytes >= size) {
					UnlinkFromBin(head, ref hdr);
					var remainder = hdr.SizeBytes - size;
					if (remainder >= SegmentedConstants.MinArenaBlockBytes) {
						var tailOffset = head + size;
						WriteHeader(tailOffset, new SegmentedFreeBlockHeader {
							SizeBytes = remainder,
							NextOffset = -1,
							PrevOffset = -1,
							BinIndex = BinIndexForSize(remainder),
						});
						LinkIntoBin(tailOffset);
					}
					ptr = new IntPtr(Buffer.ToInt64() + head);
					return true;
				}
				head = hdr.NextOffset;
			}
		}
		if (BumpOffset + size <= Capacity) {
			ptr = new IntPtr(Buffer.ToInt64() + BumpOffset);
			BumpOffset += size;
			return true;
		}
		ptr = IntPtr.Zero;
		return false;
	}

	public void Free(IntPtr ptr, int byteCount)
	{
		var offset = (int)(ptr.ToInt64() - Buffer.ToInt64());
		var size = AlignSize(byteCount);
		TryCoalesceForward(ref offset, ref size);
		TryCoalesceBackward(ref offset, ref size);
		WriteHeader(offset, new SegmentedFreeBlockHeader {
			SizeBytes = size,
			NextOffset = -1,
			PrevOffset = -1,
			BinIndex = BinIndexForSize(size),
		});
		LinkIntoBin(offset);
	}

	public void Reset()
	{
		BumpOffset = 0;
		for (var i = 0; i < binHeads.Length; i++) {
			binHeads[i] = -1;
		}
	}

	public void Dispose()
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
		if (size < SegmentedConstants.MinArenaBlockBytes) {
			return SegmentedConstants.MinArenaBlockBytes;
		}
		return (size + (alignment - 1)) & ~(alignment - 1);
	}

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
	}

	private void UnlinkFromBin(int offset, ref SegmentedFreeBlockHeader hdr)
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
	}

	private void TryCoalesceForward(ref int offset, ref int size)
	{
		var successorOffset = offset + size;
		if (successorOffset >= BumpOffset) {
			return;
		}
		for (var b = 0; b < binHeads.Length; b++) {
			var cursor = binHeads[b];
			while (cursor >= 0) {
				var hdr = ReadHeader(cursor);
				if (cursor == successorOffset) {
					UnlinkFromBin(cursor, ref hdr);
					size += hdr.SizeBytes;
					return;
				}
				cursor = hdr.NextOffset;
			}
		}
	}

	private void TryCoalesceBackward(ref int offset, ref int size)
	{
		for (var b = 0; b < binHeads.Length; b++) {
			var cursor = binHeads[b];
			while (cursor >= 0) {
				var hdr = ReadHeader(cursor);
				if (cursor + hdr.SizeBytes == offset) {
					UnlinkFromBin(cursor, ref hdr);
					offset = cursor;
					size += hdr.SizeBytes;
					return;
				}
				cursor = hdr.NextOffset;
			}
		}
	}
}
