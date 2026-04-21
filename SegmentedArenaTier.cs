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

	public SegmentedArenaTier(int segmentBytes)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, SegmentedConstants.MinArenaBlockBytes);
		defaultSegmentBytes = segmentBytes;
	}

	public int SegmentCount => segments.Count;

	public long UnmanagedBytes
	{
		get
		{
			long total = 0;
			foreach (var s in segments) {
				total += s.UnmanagedBytes;
			}

			return total;
		}
	}

	/// <summary>
	/// Allocates <paramref name="byteCount"/> bytes from the first segment that can satisfy the request.
	/// If no existing segment has room, a new one is appended. New segments are sized as
	/// <c>max(defaultSegmentBytes, byteCount)</c> so a single oversized string gets its own dedicated segment.
	/// </summary>
	public IntPtr Allocate(int byteCount, out SegmentedArenaSegment owningSegment)
	{
		foreach (var s in segments) {
			if (!s.TryAllocate(byteCount, out var ptr)) {
				continue;
			}

			owningSegment = s;
			return ptr;
		}

		var capacity = Math.Max(defaultSegmentBytes, byteCount);
		var segment = new SegmentedArenaSegment(capacity);
		segments.Add(segment);
		_ = segment.TryAllocate(byteCount, out var newPtr);
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
		var totalBytes = 0;
		foreach (var s in segments) {
			totalBytes += s.Capacity;
		}

		while (totalBytes < bytes) {
			var next = Math.Max(defaultSegmentBytes, bytes - totalBytes);
			segments.Add(new(next));
			totalBytes += next;
		}
	}

	public void Dispose()
	{
		foreach (var s in segments) {
			s.Dispose();
		}

		segments.Clear();
	}
}
