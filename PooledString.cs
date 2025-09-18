namespace LookBusy;

using System;
using System.Collections.Generic;

/*	PooledString is a small immutable struct that represents a string allocated from an UnmanagedStringPool.
	It holds a reference to the pool and an allocation ID, which together identify the actual string data in unmanaged memory.
	Because it is a struct, it has value semantics - two PooledStrings with the same content are considered equal, even if they come from different pools.

	PooledString provides methods to read the string as a ReadOnlySpan<char> for efficient access without additional allocations.
	It also has methods to manipulate the string, such as Insert and Replace, which return new PooledString instances with the modified content.
	These operations allocate new memory from the pool as needed.

	PooledString implements IDisposable to allow freeing its memory back to the pool when no longer needed.
	Double-freeing is safe - freeing an already freed PooledString has no effect.

	Think of PooledString as similar to a ReadOnlyMemory<char> that is backed by unmanaged memory from a pool, with additional string manipulation capabilities.
*/

/// <summary>
/// Value type representing a string allocated from an unmanaged pool. Just a reference and an allocation ID, 12 bytes total.
/// </summary>
[System.Diagnostics.DebuggerDisplay("{ToString(),nq}")]
public readonly record struct PooledString(UnmanagedStringPool Pool, uint AllocationId) : IDisposable
{
	// NOTE this struct is technically immutable, but some methods mutate the underlying pool like SetAtPosition() and Free()
	// It also implements IDisposable to call Free() automatically

	#region Public API

	/// <summary>
	/// Get this string as a span for efficient reading
	/// </summary>
	public readonly ReadOnlySpan<char> AsSpan()
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return []; // Empty string
		}

		CheckDisposed();

		var info = Pool.GetAllocationInfo(AllocationId); // this will throw if the ID has been freed
		unsafe {
			return new((void*)info.Pointer, info.LengthChars);
		}
	}

	/// <summary>
	/// Free this string's memory back to the pool. This doesn't mutate the actual PooledString fields, it just updates the underlying pool
	/// </summary>
	public readonly void Free() => Pool?.FreeString(AllocationId);

	/// <summary>
	/// Allocate a new PooledString with the given value at the specified position. Old PooledString is unchanged.
	/// </summary>
	public readonly PooledString Insert(int pos, ReadOnlySpan<char> value)
	{
		if (value.IsEmpty) {
			return this;
		}

		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId && pos != 0) {
			throw new ArgumentOutOfRangeException(nameof(pos), "Cannot insert into an empty string at position other than 0");
		}

		CheckDisposed();

		var currentSpan = AsSpan();
		if (pos < 0 || pos > currentSpan.Length) {
			throw new ArgumentOutOfRangeException(nameof(pos), "Position is out of bounds");
		}

		// Allocate a new string of the required total size
		var result = Pool.Allocate(currentSpan.Length + value.Length);

		// Copy the three parts directly into the new buffer
		var beforeInsert = currentSpan[..pos];
		var afterInsert = currentSpan[pos..];

		// First part: characters before the insertion point
		if (beforeInsert.Length > 0) {
			result.SetAtPosition(0, beforeInsert);
		}

		// Middle part: the value to insert
		result.SetAtPosition(pos, value);

		// Last part: characters after the insertion point
		if (afterInsert.Length > 0) {
			result.SetAtPosition(pos + value.Length, afterInsert);
		}

		return result;
	}

	/// <summary>
	/// Returns the zero-based index of the first occurrence of the specified string
	/// </summary>
	public readonly int IndexOf(ReadOnlySpan<char> value, StringComparison comparison = StringComparison.Ordinal)
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return value.IsEmpty ? 0 : -1;
		}

		return AsSpan().IndexOf(value, comparison);
	}

	/// <summary>
	/// Returns the zero-based index of the last occurrence of the specified string
	/// </summary>
	public readonly int LastIndexOf(ReadOnlySpan<char> value, StringComparison comparison = StringComparison.Ordinal)
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return value.IsEmpty ? 0 : -1;
		}

		return AsSpan().LastIndexOf(value, comparison);
	}

	/// <summary>
	/// Determines whether this string starts with the specified string
	/// </summary>
	public readonly bool StartsWith(ReadOnlySpan<char> value, StringComparison comparison = StringComparison.Ordinal)
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return value.IsEmpty;
		}

		return AsSpan().StartsWith(value, comparison);
	}

	/// <summary>
	/// Determines whether this string ends with the specified string
	/// </summary>
	public readonly bool EndsWith(ReadOnlySpan<char> value, StringComparison comparison = StringComparison.Ordinal)
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return value.IsEmpty;
		}

		return AsSpan().EndsWith(value, comparison);
	}

	/// <summary>
	/// Determines whether this string contains the specified string
	/// </summary>
	public readonly bool Contains(ReadOnlySpan<char> value, StringComparison comparison = StringComparison.Ordinal)
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return value.IsEmpty;
		}

		return AsSpan().Contains(value, comparison);
	}

	/// <summary>
	/// Gets the length of this string in characters
	/// </summary>
	public readonly int Length
	{
		get
		{
			if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
				return 0;
			}

			CheckDisposed();
			var info = Pool.GetAllocationInfo(AllocationId);
			return info.LengthChars;
		}
	}

	/// <summary>
	/// Determines whether this string is empty
	/// </summary>
	public readonly bool IsEmpty => AllocationId == UnmanagedStringPool.EmptyStringAllocationId || Length == 0;

	/// <summary>
	/// Extract a substring from this string, just a convenience method for AsSpan().Slice()
	/// </summary>
	public readonly ReadOnlySpan<char> SubstringSpan(int startIndex, int length)
	{
		var span = AsSpan();

		if (startIndex < 0 || startIndex > span.Length) {
			throw new ArgumentOutOfRangeException(nameof(startIndex), $"Start index {startIndex} is out of range for string of length {span.Length}");
		}

		if (length < 0 || startIndex + length > span.Length) {
			throw new ArgumentOutOfRangeException(nameof(length),
				$"Length {length} from start index {startIndex} exceeds string length {span.Length}");
		}

		return span.Slice(startIndex, length);
	}

	/// <summary>
	/// Replace all occurrences of a substring with another string
	/// </summary>
	public readonly PooledString Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue)
	{
		CheckDisposed();

		if (oldValue.IsEmpty) {
			return this;
		}

		var span = AsSpan();
		if (span.Length == 0) {
			return this;
		}

		// Single pass: find occurrences and track positions
		var occurrences = new List<int>();
		var pos = 0;
		while (pos < span.Length) {
			var foundIndex = span[pos..].IndexOf(oldValue);
			if (foundIndex < 0) {
				break;
			}

			occurrences.Add(pos + foundIndex);
			pos += foundIndex + oldValue.Length;
		}

		if (occurrences.Count == 0) {
			return this; // Nothing to replace
		}

		// Calculate new size and check for overflow
		var sizeDiff = newValue.Length - oldValue.Length;
		if (sizeDiff > 0 && occurrences.Count > 0) {
			// Check if the total increase would cause overflow
			// We want to avoid: span.Length + sizeDiff * occurrences.Count > int.MaxValue
			// Rearranged: sizeDiff * occurrences.Count > int.MaxValue - span.Length
			if (occurrences.Count > (int.MaxValue - span.Length) / sizeDiff) {
				throw new ArgumentException("Replacement would result in string too large");
			}
		}

		var newSize = span.Length + (sizeDiff * occurrences.Count);
		if (newSize < 0) {
			throw new ArgumentException("Replacement would result in invalid size");
		}

		var result = Pool.Allocate(newSize);

		// Perform replacements in a single pass using tracked positions
		var srcPos = 0;
		var destPos = 0;

		foreach (var occurrencePos in occurrences) {
			// Copy text before match
			var before = span[srcPos..occurrencePos];
			if (before.Length > 0) {
				result.SetAtPosition(destPos, before);
				destPos += before.Length;
			}

			// Copy replacement
			if (newValue.Length > 0) {
				result.SetAtPosition(destPos, newValue);
				destPos += newValue.Length;
			}

			// Update source position
			srcPos = occurrencePos + oldValue.Length;
		}

		// Copy remaining text
		if (srcPos < span.Length) {
			result.SetAtPosition(destPos, span[srcPos..]);
		}

		return result;
	}

	/// <summary>
	/// Value semantics comparison. Note this is a record struct, so == and != are already implemented
	/// We override Equals to compare content rather than pool and allocation ID
	/// </summary>
	public readonly bool Equals(PooledString other)
	{
#pragma warning disable IDE0046 // Convert to conditional expression
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId &&
			other.AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return true;
		}
#pragma warning restore IDE0046 // Convert to conditional expression

		// Check for null or disposed pools before attempting to get spans
		if (Pool == null || Pool.IsDisposed || other.Pool == null || other.Pool.IsDisposed) {
			return false;
		}

		// compare as spans
		return AsSpan().Equals(other.AsSpan(), StringComparison.Ordinal);
	}

	/// <summary>
	/// Convert to standard .NET string (allocates managed memory)
	/// </summary>
	public override readonly string ToString() => AsSpan().ToString();

	/// <summary>
	/// Hash code based on content
	/// </summary>
	public override readonly int GetHashCode()
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return 0;
		}

		if (Pool.IsDisposed) {
			return -1;
		}

		const int maxChars = 64;
		const int halfMax = maxChars / 2;
		var span = AsSpan();
		var hash = new HashCode();

		if (span.Length <= maxChars) {
			// Hash all characters
			foreach (var c in span) {
				hash.Add(c);
			}
		} else {
			// Hash first fragment and last fragment chars
			foreach (var c in span[..halfMax]) {
				hash.Add(c);
			}

			foreach (var c in span[^halfMax..]) {
				hash.Add(c);
			}
		}

		return hash.ToHashCode();
	}

	#endregion // public API

	/// <summary>
	/// Checks if the underlying pool is disposed before performing any operations
	/// </summary>
	private readonly void CheckDisposed()
	{
		if (Pool.IsDisposed) {
			throw new ObjectDisposedException(nameof(PooledString));
		}
	}

	/// <summary>
	/// Internal mutate method to set part of the buffer. Note this doesn't actually mutate the PooledString itself, just the underlying pool.
	/// </summary>
	private readonly void SetAtPosition(int start, ReadOnlySpan<char> value)
	{
		CheckDisposed();

		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			throw new InvalidOperationException("Cannot mutate an empty string allocation");
		}

		// Get the current allocation info
		var info = Pool.GetAllocationInfo(AllocationId);

		// Check if the starting position is valid
		if (start < 0) {
			throw new ArgumentOutOfRangeException(nameof(start), "Start position cannot be negative");
		}

		// Check if the value will fit in the buffer
		if (start + value.Length > info.LengthChars) {
			throw new ArgumentOutOfRangeException(
				nameof(value),
				$"The provided value is too large to fit in the string at the specified position. Available space: {info.LengthChars - start}, required: {value.Length}");
		}

		// Copy the value to the target position
		unsafe {
			fixed (char* pChar = value) {
				var dest = (void*)IntPtr.Add(info.Pointer, start * sizeof(char));
				Buffer.MemoryCopy(pChar, dest, (info.LengthChars - start) * sizeof(char), value.Length * sizeof(char));
			}
		}
	}

	/// <summary>
	/// Free the string back to the pool, if it is not empty
	/// </summary>
	public void Dispose() => Free();
}
