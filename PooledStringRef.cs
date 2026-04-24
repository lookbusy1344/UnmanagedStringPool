namespace LookBusy;

using System;
using System.Buffers;

/// <summary>
/// 16-byte handle to a string in a <see cref="SegmentedStringPool"/>. <see cref="default"/> is
/// the empty sentinel; real allocations have generation ≥ 1. Content-based equality.
/// <para>
/// Disposing any copy invalidates all copies via generation bump on free.
/// </para>
/// </summary>
public readonly struct PooledStringRef : IDisposable, IEquatable<PooledStringRef>
{
	// Stack-allocate match positions for up to this many occurrences before renting from ArrayPool.
	private const int ReplaceInlineMatchCap = 64;

	internal PooledStringRef(SegmentedStringPool? pool, uint slotIndex, uint generation)
	{
		Pool = pool;
		SlotIndex = slotIndex;
		Generation = generation;
	}

	public SegmentedStringPool? Pool { get; }
	public uint SlotIndex { get; }
	public uint Generation { get; }

	public static PooledStringRef Empty => default;

	// All three fields must be checked: a non-null Pool with SlotIndex=0 and Generation=0 would be a valid slot 0
	// whose generation counter hasn't been bumped yet (fresh pool, no alloc) — that is not the empty sentinel.
	public bool IsEmpty => Pool is null && SlotIndex == 0u && Generation == 0u;

	public int Length => IsEmpty ? 0 : Pool!.GetLength(SlotIndex, Generation);

	public ReadOnlySpan<char> AsSpan() => IsEmpty ? [] : Pool!.ReadSlot(SlotIndex, Generation);

	public void Free() => Pool?.FreeSlot(SlotIndex, Generation);

	public void Dispose() => Free();

	// ---- query methods ----

	public int IndexOf(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
		IsEmpty ? (value.IsEmpty ? 0 : -1) : AsSpan().IndexOf(value, c);

	public int LastIndexOf(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
		IsEmpty ? (value.IsEmpty ? 0 : -1) : AsSpan().LastIndexOf(value, c);

	public bool StartsWith(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
		IsEmpty ? value.IsEmpty : AsSpan().StartsWith(value, c);

	public bool EndsWith(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
		IsEmpty ? value.IsEmpty : AsSpan().EndsWith(value, c);

	public bool Contains(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
		IsEmpty ? value.IsEmpty : AsSpan().Contains(value, c);

	public ReadOnlySpan<char> SubstringSpan(int startIndex, int length)
	{
		var span = AsSpan();
		if ((uint)startIndex > (uint)span.Length) {
			throw new ArgumentOutOfRangeException(nameof(startIndex));
		}
		if ((uint)length > (uint)(span.Length - startIndex)) {
			throw new ArgumentOutOfRangeException(nameof(length));
		}
		return span.Slice(startIndex, length);
	}

	// ---- mutation methods (produce new allocation) ----

	public PooledStringRef Duplicate()
	{
		if (IsEmpty) {
			return Empty;
		}
		return Pool!.Allocate(AsSpan());
	}

	public PooledStringRef Insert(int index, ReadOnlySpan<char> value)
	{
		if (Pool is null) {
			return Empty;
		}
		var original = AsSpan();
		if ((uint)index > (uint)original.Length) {
			throw new ArgumentOutOfRangeException(nameof(index));
		}
		if (value.IsEmpty) {
			return Duplicate();
		}
		var totalLength = original.Length + value.Length;
		char[]? rented = null;
		Span<char> buffer = totalLength <= 256
			? stackalloc char[totalLength]
			: (rented = ArrayPool<char>.Shared.Rent(totalLength)).AsSpan(0, totalLength);

		original.Slice(0, index).CopyTo(buffer);
		value.CopyTo(buffer.Slice(index));
		original.Slice(index).CopyTo(buffer.Slice(index + value.Length));

		try {
			return Pool.Allocate(buffer);
		}
		finally {
			if (rented is not null) {
				ArrayPool<char>.Shared.Return(rented);
			}
		}
	}

	public PooledStringRef Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue)
	{
		if (Pool is null) {
			return Empty;
		}
		if (oldValue.IsEmpty) {
			throw new ArgumentException("oldValue cannot be empty.", nameof(oldValue));
		}
		var source = AsSpan();
		if (source.IsEmpty) {
			return Empty;
		}

		Span<int> inlineMatches = stackalloc int[ReplaceInlineMatchCap];
		int[]? rentedMatches = null;
		Span<int> matches = inlineMatches;
		var matchCount = 0;

		var searchStart = 0;
		while (searchStart <= source.Length - oldValue.Length) {
			var found = source.Slice(searchStart).IndexOf(oldValue);
			if (found < 0) {
				break;
			}
			var absolute = searchStart + found;
			if (matchCount == matches.Length) {
				var newSize = matches.Length * 2;
				var nextRented = ArrayPool<int>.Shared.Rent(newSize);
				matches.Slice(0, matchCount).CopyTo(nextRented);
				if (rentedMatches is not null) {
					ArrayPool<int>.Shared.Return(rentedMatches);
				}
				rentedMatches = nextRented;
				matches = rentedMatches;
			}
			matches[matchCount++] = absolute;
			searchStart = absolute + oldValue.Length;
		}

		if (matchCount == 0) {
			if (rentedMatches is not null) {
				ArrayPool<int>.Shared.Return(rentedMatches);
			}
			return Duplicate();
		}

		var totalLength = source.Length + (matchCount * (newValue.Length - oldValue.Length));
		char[]? rentedChars = null;
		Span<char> buffer = totalLength <= 256
			? stackalloc char[totalLength]
			: (rentedChars = ArrayPool<char>.Shared.Rent(totalLength)).AsSpan(0, totalLength);

		var srcCursor = 0;
		var dstCursor = 0;
		for (var i = 0; i < matchCount; ++i) {
			var matchAt = matches[i];
			var preLen = matchAt - srcCursor;
			source.Slice(srcCursor, preLen).CopyTo(buffer.Slice(dstCursor));
			dstCursor += preLen;
			newValue.CopyTo(buffer.Slice(dstCursor));
			dstCursor += newValue.Length;
			srcCursor = matchAt + oldValue.Length;
		}
		source.Slice(srcCursor).CopyTo(buffer.Slice(dstCursor));

		try {
			return Pool.Allocate(buffer);
		}
		finally {
			if (rentedMatches is not null) {
				ArrayPool<int>.Shared.Return(rentedMatches);
			}
			if (rentedChars is not null) {
				ArrayPool<char>.Shared.Return(rentedChars);
			}
		}
	}

	// ---- equality + hash ----

	public bool Equals(PooledStringRef other) =>
		AsSpan().SequenceEqual(other.AsSpan());

	public override bool Equals(object? obj) => obj switch {
		PooledStringRef r => Equals(r),
		string s => AsSpan().SequenceEqual(s.AsSpan()),
		_ => false,
	};

	// Intentionally samples only the first and last 8 chars plus the length to keep hashing O(1) for large strings.
	// This is a speed/distribution tradeoff: hash quality degrades for strings that differ only in the middle,
	// but avoids O(n) cost for very long strings stored in the pool.
	public override int GetHashCode()
	{
		var span = AsSpan();
		if (span.IsEmpty) {
			return 0;
		}
		var hc = new HashCode();
		hc.Add(span.Length);
		var prefix = span.Slice(0, Math.Min(8, span.Length));
		foreach (var ch in prefix) {
			hc.Add(ch);
		}
		if (span.Length > 8) {
			var suffix = span.Slice(Math.Max(span.Length - 8, 8));
			foreach (var ch in suffix) {
				hc.Add(ch);
			}
		}
		return hc.ToHashCode();
	}

	public override string ToString() => AsSpan().ToString();

	public static bool operator ==(PooledStringRef left, PooledStringRef right) => left.Equals(right);
	public static bool operator !=(PooledStringRef left, PooledStringRef right) => !left.Equals(right);
}
