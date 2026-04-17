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
		if (segmentBytes < SegmentedConstants.MinArenaBlockBytes) {
			throw new ArgumentOutOfRangeException(nameof(segmentBytes));
		}
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

	public IntPtr Allocate(int byteCount, out SegmentedArenaSegment owningSegment)
	{
		foreach (var s in segments) {
			if (s.TryAllocate(byteCount, out var ptr)) {
				owningSegment = s;
				return ptr;
			}
		}
		var capacity = Math.Max(defaultSegmentBytes, byteCount);
		var segment = new SegmentedArenaSegment(capacity);
		segments.Add(segment);
		_ = segment.TryAllocate(byteCount, out var newPtr);
		owningSegment = segment;
		return newPtr;
	}

	public static void Free(IntPtr ptr, int byteCount, SegmentedArenaSegment segment) =>
		segment.Free(ptr, byteCount);

	public SegmentedArenaSegment LocateSegmentByPointer(IntPtr ptr)
	{
		foreach (var s in segments) {
			if (s.Contains(ptr)) {
				return s;
			}
		}
		throw new InvalidOperationException("Pointer does not belong to any arena segment");
	}

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
			segments.Add(new SegmentedArenaSegment(next));
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
