// ReSharper disable ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
namespace LookBusy;

using System;
using System.Collections.Generic;

/// <summary>
/// Owns the list of <see cref="SegmentedArenaSegment"/> instances and routes allocation across
/// them. New segments are added when existing ones can't satisfy a request. Never resizes or
/// moves an existing segment.
/// </summary>
internal sealed class SegmentedArenaTier : IDisposable
{
	private readonly List<SegmentedArenaSegment> segments = [];
	private readonly int defaultSegmentBytes;
	private bool disposed;

	public SegmentedArenaTier(int segmentBytes)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, SegmentedConstants.MinArenaBlockBytes);
		defaultSegmentBytes = segmentBytes;
	}

	public int SegmentCount => segments.Count;

	public long GetUnmanagedBytes()
	{
		long total = 0;
		foreach (var s in segments) {
			total += s.UnmanagedBytes;
		}

		return total;
	}

	/// <summary>
	/// Allocates <paramref name="byteCount"/> bytes from the first segment that can satisfy the request.
	/// If no existing segment has room, a new one is appended. New segments are sized as
	/// <c>max(defaultSegmentBytes, byteCount)</c> so a single oversized string gets its own dedicated segment.
	/// <para>
	/// <paramref name="allocatedBytes"/> receives the true bytes handed out (see
	/// <see cref="SegmentedArenaSegment.TryAllocate"/>). The caller must store this value and supply it
	/// when freeing so no-split slack is not orphaned.
	/// </para>
	/// </summary>
	public IntPtr Allocate(int byteCount, out SegmentedArenaSegment owningSegment, out int allocatedBytes)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		var normalizedByteCount = SegmentedArenaSegment.NormalizeAllocationBytes(byteCount);
		var isOversizedRequest = normalizedByteCount > defaultSegmentBytes;
		foreach (var s in segments) {
			// Skip dedicated oversized segments for normal requests — mixing small and large
			// allocations into an oversized segment defeats its "dedicated" purpose and causes
			// fragmentation in a segment that the large allocation was supposed to own entirely.
			if (s.IsOversized != isOversizedRequest) {
				continue;
			}

			if (!s.TryAllocate(normalizedByteCount, out var ptr, out var actual)) {
				continue;
			}

			owningSegment = s;
			allocatedBytes = actual;
			return ptr;
		}

		var capacity = Math.Max(defaultSegmentBytes, normalizedByteCount);
		var segment = new SegmentedArenaSegment(capacity) { IsOversized = isOversizedRequest };
		segments.Add(segment);
		if (!segment.TryAllocate(normalizedByteCount, out var newPtr, out allocatedBytes)) {
			throw new InvalidOperationException("Fresh arena segment could not satisfy the requested allocation");
		}
		owningSegment = segment;
		return newPtr;
	}

	/// <summary>
	/// Frees a block back to its owning segment. The caller must supply the segment directly
	/// (resolved by <see cref="LocateSegmentByPointer"/>) so this method stays O(1).
	/// </summary>
	public static void Free(IntPtr ptr, int byteCount, SegmentedArenaSegment segment) =>
		segment.Free(ptr, byteCount);

	/// <summary>
	/// Returns the segment that owns <paramref name="ptr"/>. O(number of segments).
	/// Called only during <see cref="Free"/>, not on the read path.
	/// </summary>
	public SegmentedArenaSegment LocateSegmentByPointer(IntPtr ptr)
	{
		foreach (var s in segments) {
			if (s.Contains(ptr)) {
				return s;
			}
		}

		throw new InvalidOperationException("Pointer does not belong to any arena segment");
	}

	/// <summary>
	/// Resets every segment's bump pointer and bin heads without freeing unmanaged memory.
	/// Used by <c>pool.Clear()</c>; segments are reused for subsequent allocations.
	/// </summary>
	public void ResetAll()
	{
		foreach (var s in segments) {
			s.Reset();
		}
	}

	public void Reserve(int bytes)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		ArgumentOutOfRangeException.ThrowIfNegative(bytes);
		long totalBytes = 0;
		foreach (var s in segments) {
			totalBytes += s.Capacity;
		}

		while (totalBytes < bytes) {
			var next = (int)Math.Max(defaultSegmentBytes, bytes - totalBytes);
			segments.Add(new(next));
			totalBytes += next;
		}
	}

	public void Dispose()
	{
		if (disposed) { return; }

		disposed = true;
		foreach (var s in segments) {
			s.Dispose();
		}

		segments.Clear();
	}
}
