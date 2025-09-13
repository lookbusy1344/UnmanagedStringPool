namespace Playground;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/*	A string-pool than uses a single block of unmanaged memory, to reduce GC load.
	A UnmanagedStringPool allocates a buffer of unmanaged memory, and individual string allocations take the form of PooledString
	structs, pointing into the pool. The pool implements IDisposable to deterministically free the pool and invalidate any PooledStrings

	Rationale:

	- Finalizers are needed to ensure unmanaged memory cleanup, but structs don't support them. We need a class.
	- A class-per-string would create significant GC load (even if the strings were stored in unmanaged memory), so instead the
	  finalizable class represents a 'pool', which can hold several strings and performs just one unmanaged memory allocation.
	- Instances of individual pooled strings are structs, pointing into a pool object. They have full copy semantics and don't involve any
	  heap allocation.

	- The pool implements IDisposable, with a finalizer, for memory safety.
	- Invalid pointers are never dereferenced. If the pool is disposed, any string structs relying on it automatically become invalid.
	  The pool is deterministically freed, but the tiny pool object itself gets GC's normally (about <100 bytes)
	- If the string within the pool is freed, the 'allocation id' is not reused so any string structs pointing to it become invalid. Reusing
	  the memory in the pool will result in a different id, preventing old string structs pointing to the new string.
	- Freed space in the pool is reused where possible, and periodically compacted
 */

public static class StringDemo
{
	public static void Run()
	{
		using var pool = new UnmanagedStringPool(4096);

		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");
		var str3 = pool.Allocate("This is a longer string");
		var strX = pool.Allocate("Hello");
		Console.WriteLine($"Value semantics, does {str1} == {strX}? {str1 == strX}");

		WriteLine(str1); // Hello
		WriteLine(str2); // World
		WriteLine(str3); // This is a longer string

		Console.WriteLine($"Free space before: {pool.FreeSpaceChars}");
		Console.WriteLine($"End block space: {pool.EndBlockSizeChars}");
		Console.WriteLine($"Active allocations: {pool.ActiveAllocations}");
		Console.WriteLine($"Fragmentation: {pool.FragmentationPercentage:F2}%");

		// Free str2 and show we can reuse its space
		str3.Dispose();
		str2.Dispose();

		using var str4 = pool.Allocate("New"); // Should reuse str2's space
		var str5 = str1.Insert(5, " Beautiful"); // Insert into str1

		Console.WriteLine("Before pool growth:");
		Console.WriteLine($"Free space after: {pool.FreeSpaceChars}");
		Console.WriteLine($"End block space: {pool.EndBlockSizeChars}");
		Console.WriteLine($"Active allocations: {pool.ActiveAllocations}");
		Console.WriteLine($"Fragmentation: {pool.FragmentationPercentage:F2}%");

		pool.DefragmentAndGrowPool(100); // Force a defragmentation and growth
		var str6 = pool.Allocate("Another string after growth");

		Console.WriteLine("After pool growth:");
		Console.WriteLine($"Free space after: {pool.FreeSpaceChars}");
		Console.WriteLine($"End block space: {pool.EndBlockSizeChars}");
		Console.WriteLine($"Active allocations: {pool.ActiveAllocations}");
		Console.WriteLine($"Fragmentation: {pool.FragmentationPercentage:F2}%");

		WriteLine(str1); // Hello
		WriteLine(str4); // New
		WriteLine(str5); // Hello Beautiful

		// Demonstrate substring operation
		var substr = str5.SubstringSpan(2, 6);
		Console.Out.WriteLine(substr); // llo Be

		// Demonstrate replace operation
		var replaced = str5.Replace("Beautiful", "Wonderful");
		WriteLine(replaced); // Hello Wonderful

		Console.WriteLine($"Buffer dump: \"{pool.DumpBufferAsString()}\"");

		var last = PooledString.Empty;
		for (var i = 0; i < 100; ++i) {
			var randomStr = RandomString(Random.Shared.Next(5, 150));
			var pooledStr = pool.Allocate(randomStr);
			WriteLine(pooledStr); // Print random string

			if (Random.Shared.Next(3) == 0) {
				// sometimes free the last string, this is intended to cause fragmentation
				last.Dispose();
			}

			last = pooledStr; // Keep the last allocated string
		}

		Console.WriteLine();
		Console.WriteLine("Final pool state:");
		Console.WriteLine($"Free space after: {pool.FreeSpaceChars}");
		Console.WriteLine($"End block space: {pool.EndBlockSizeChars}");
		Console.WriteLine($"Active allocations: {pool.ActiveAllocations}");
		Console.WriteLine($"Fragmentation: {pool.FragmentationPercentage:F2}%");
		Console.WriteLine($"Buffer dump: \"{pool.DumpBufferAsString()}\"");
	}

	private static void WriteLine(PooledString str)
	{
		Console.Out.WriteLine(str.AsSpan());
	}

	private static string RandomString(int length)
	{
		if (length <= 0) {
			return string.Empty;
		}

		const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
		var buffer = length <= 256 ? stackalloc char[length] : new char[length];

		for (var i = 0; i < length; i++) {
			buffer[i] = chars[Random.Shared.Next(chars.Length)];
		}

		return new(buffer);
	}
}

/// <summary>
/// Represents a pool for allocating unmanaged memory to store strings with automatic growth capability.
///
/// Thread Safety: This class follows standard .NET collection thread safety patterns:
/// - Multiple threads can safely read concurrently (via PooledString operations)
/// - Mutations (Allocate, Free, DefragmentAndGrowPool) require external synchronization
/// - Disposing the pool while strings are in use is unsafe
/// </summary>
public class UnmanagedStringPool : IDisposable
{
	public const int EmptyStringAllocationId = 0; // Reserved for empty strings
	private const int DefaultCollectionSize = 16; // Default size for internal collections
	private const double FragmentationThreshold = 35.0; // Fragmentation threshold for triggering coalescing (percentage)
	private const int MinimumBlocksForCoalescing = 8; // Minimum number of free blocks before considering coalescing
	private const double GrowthFactor = 1.5; // Default growth factor when pool needs to expand
	private const int MinimumFreesBetweenCoalescing = 10; // Minimum number of free operations before coalescing

	private IntPtr basePtr; // Base pointer to the unmanaged memory block
	private int capacityBytes; // Total capacity in bytes of the pool
	private int offsetFromBase; // Current offset from the base pointer, tracks how much space has been used
	private int lastAllocationId; // Last allocation ID used, starts at 0. Monotonically increasing
	private int freeOperationsSinceLastCoalesce; // Track recent coalescing to avoid excessive operations
	private int totalFreeBlocks; // Total number of free blocks in the pool
	private int totalFreeBytes; // Running total of free bytes to avoid recalculation

	// Index free blocks by size for faster allocation
	private readonly SortedList<int, List<FreeBlock>> freeBlocksBySize = new(DefaultCollectionSize);

	// Central registry of allocated strings
	private readonly Dictionary<int, AllocationInfo> allocations = new(DefaultCollectionSize);

	/// <summary>
	/// Information about an allocated string. OffsetBytes is a convenience field, for when the block is freed. 16 bytes total
	/// </summary>
	internal readonly record struct AllocationInfo(IntPtr Pointer, int LengthChars, int OffsetBytes)
	{
		/// <summary>
		/// Just to aid debugging, this is an internal struct and not intended for public use
		/// </summary>
		public override unsafe string ToString()
		{
			return new((char*)Pointer, 0, LengthChars);
		}
	}

	/// <summary>
	/// Information about a free block in the pool, 8 bytes total.
	/// </summary>
	private readonly record struct FreeBlock(int OffsetFromBase, int SizeBytes);

	#region Public API

	/// <summary>
	/// Creates a new string pool with the specified initial capacity
	/// </summary>
	/// <param name="initialCapacityChars">Initial capacity in characters</param>
	/// <param name="allowGrowth">Whether to allow the pool to grow automatically when needed</param>
	public UnmanagedStringPool(int initialCapacityChars, bool allowGrowth = true)
	{
		if (initialCapacityChars < 1) {
			throw new ArgumentOutOfRangeException(nameof(initialCapacityChars), "Capacity must be positive");
		}

		capacityBytes = initialCapacityChars * sizeof(char);
		basePtr = Marshal.AllocHGlobal(capacityBytes);
		offsetFromBase = 0;
		AllowGrowth = allowGrowth;
	}

	/// <summary>
	/// Gets the amount of free space remaining in the pool in chars
	/// </summary>
	public int FreeSpaceChars => (capacityBytes - offsetFromBase + totalFreeBytes) / sizeof(char);

	/// <summary>
	/// Gets the number of characters that can fit in the remaining end block
	/// </summary>
	public int EndBlockSizeChars => (capacityBytes - offsetFromBase) / sizeof(char);

	/// <summary>
	/// Gets the number of active string allocations in the pool
	/// </summary>
	public int ActiveAllocations => allocations.Count;

	/// <summary>
	/// Gets the current fragmentation percentage of the pool (0-100)
	/// </summary>
	public double FragmentationPercentage => totalFreeBytes * 100.0 / capacityBytes;

	/// <summary>
	/// Gets or sets whether the pool is allowed to grow automatically when needed
	/// </summary>
	public bool AllowGrowth { get; set; }

	/// <summary>
	/// Allocates a string of the specified length from the unmanaged string pool, and populate with the given ReadOnlySpan
	/// </summary>
	public PooledString Allocate(ReadOnlySpan<char> value)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, typeof(UnmanagedStringPool));

		if (value.IsEmpty) {
			return PooledString.Empty;
		}

		// allocate a buffer for this string
		var result = Allocate(value.Length);
		var info = GetAllocationInfo(result.AllocationId);

		// get the offset and byte length
		var offset = info.OffsetBytes;
		var byteLength = value.Length * sizeof(char);

		unsafe {
			fixed (char* pChar = value) {
				var dest = (void*)IntPtr.Add(basePtr, offset);
				Buffer.MemoryCopy(pChar, dest, byteLength, byteLength);
			}
		}

		return result;
	}

	/// <summary>
	/// Grows the pool safely by allocating a new buffer and copying only active allocations
	/// </summary>
	public void DefragmentAndGrowPool(int additionalBytes)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, typeof(UnmanagedStringPool));

		if (additionalBytes < 0) {
			throw new ArgumentOutOfRangeException(nameof(additionalBytes), "Additional bytes must be zero or positive");
		}

		// Check for integer overflow
		if ((long)capacityBytes + additionalBytes > int.MaxValue) {
			throw new ArgumentOutOfRangeException(nameof(additionalBytes), "New capacity would exceed maximum size");
		}

		var newCapacity = capacityBytes + additionalBytes;
		var newPtr = Marshal.AllocHGlobal(newCapacity);
		var newOffset = 0;

		try {
			// Copy active allocations to the new buffer sequentially
			foreach (var (id, info) in allocations) {
				var lengthBytes = info.LengthChars * sizeof(char);
				var alignedLengthBytes = AlignSize(lengthBytes);

				unsafe {
					var dest = (void*)IntPtr.Add(newPtr, newOffset);
					Buffer.MemoryCopy((void*)info.Pointer, dest, lengthBytes, lengthBytes);
				}

				allocations[id] = new(newPtr + newOffset, info.LengthChars, newOffset);
				newOffset += alignedLengthBytes; // Use aligned size for proper spacing
			}

			// Update pool state
			Marshal.FreeHGlobal(basePtr);
			basePtr = newPtr;
			newPtr = IntPtr.Zero; // Prevent double free
			totalFreeBlocks = 0;
			totalFreeBytes = 0;
			capacityBytes = newCapacity;
			offsetFromBase = newOffset;
			freeBlocksBySize.Clear();
		}
		catch {
			Marshal.FreeHGlobal(newPtr); // can safely call Marshal.FreeHGlobal(newPtr) even if newPtr is zero
			throw;
		}
	}

	/// <summary>
	/// Diagnostic: Returns the entire buffer as a string, up to the last allocated character.
	/// This includes all bytes from the start of the pool up to offsetFromBase.
	/// </summary>
	public string DumpBufferAsString()
	{
		ObjectDisposedException.ThrowIf(IsDisposed, typeof(UnmanagedStringPool));
		unsafe {
			return offsetFromBase == 0 ? string.Empty : new((char*)basePtr, 0, offsetFromBase / sizeof(char));
		}
	}

	#endregion

	#region Friend API for interaction with PooledString, not for public use

	/// <summary>
	/// Allocates a string of the specified length from the unmanaged string pool. Leaves it uninitialized
	/// </summary>
	internal PooledString Allocate(int lengthChars)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, typeof(UnmanagedStringPool));

		if (lengthChars <= 0) {
			return PooledString.Empty;
		}

		// Check for overflow when converting to bytes and aligning
		// We need to ensure that (lengthChars * sizeof(char) + alignment - 1) won't overflow
		const int alignment = 8;
		var maxSafeLength = (int.MaxValue - alignment + 1) / sizeof(char);
		if (lengthChars > maxSafeLength) {
			throw new ArgumentOutOfRangeException(nameof(lengthChars), "String length would cause integer overflow");
		}

		var byteLength = AlignSize(lengthChars * sizeof(char));

		// Try to find a suitable free block first
		if (FindSuitableFreeBlock(byteLength, out var block)) {
			RemoveFreeBlock(block);

			var allocPtr = basePtr + block.OffsetFromBase;

			// If the block is larger than needed, return the remainder
			var remainingSize = block.SizeBytes - byteLength;
			if (remainingSize >= sizeof(char)) {
				var newBlock = new FreeBlock(block.OffsetFromBase + byteLength, remainingSize);
				AddFreeBlock(newBlock);
			}

			return RegisterAllocation(allocPtr, lengthChars, block.OffsetFromBase);
		}

		// No suitable free block, allocate at the end
		if (offsetFromBase + byteLength > capacityBytes) {
			if (AllowGrowth) {
				DefragmentAndGrowPool(Math.Max(byteLength, (int)(capacityBytes * GrowthFactor)));
			} else {
				throw new OutOfMemoryException("Pool exhausted and growth is disabled");
			}
		}

		var result = RegisterAllocation(basePtr + offsetFromBase, lengthChars, offsetFromBase);
		offsetFromBase += byteLength;

		return result;
	}

	/// <summary>
	/// Get allocation info for a valid allocation ID
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal virtual AllocationInfo GetAllocationInfo(int id)
	{
		return allocations.TryGetValue(id, out var info)
			? info
			: throw new ArgumentException($"Invalid allocation ID: {id}", nameof(id));
	}

	/// <summary>
	/// Mark a string's memory as free for reuse
	/// </summary>
	internal virtual void FreeString(int id)
	{
		if (IsDisposed || id == EmptyStringAllocationId) {
			// Empty strings do not need to be freed, they are always available
			return;
		}

		// if not found, just ignore
		if (allocations.TryGetValue(id, out var info)) {
			var byteLength = AlignSize(info.LengthChars * sizeof(char));
			AddFreeBlock(new(info.OffsetBytes, byteLength));

			// Remove from allocations dictionary
			_ = allocations.Remove(id);

			++freeOperationsSinceLastCoalesce;

			// DEBUG CODE - always coalesce for testing purposes
			//CoalesceFreeBlocks();
			//freeOperationsSinceLastCoalesce = 0;

			if (FragmentationPercentage > FragmentationThreshold &&
				totalFreeBlocks >= MinimumBlocksForCoalescing &&
				freeOperationsSinceLastCoalesce >= MinimumFreesBetweenCoalescing) {
				// If fragmentation is high, we have enough free blocks, and we have freed enough strings since last coalesce
				CoalesceFreeBlocks();
				freeOperationsSinceLastCoalesce = 0;
			}
		}
	}

	#endregion

	#region Private methods

	/// <summary>
	/// Align to 8 bytes for optimal memory usage while maintaining alignment
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int AlignSize(int sizeBytes)
	{
		const int alignment = 8;
		return sizeBytes < alignment ? alignment : (sizeBytes + (alignment - 1)) & ~(alignment - 1);
	}

	/// <summary>
	/// Register an allocation in the collection
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private PooledString RegisterAllocation(IntPtr ptr, int lengthChars, int offset)
	{
		++lastAllocationId;
		allocations[lastAllocationId] = new(ptr, lengthChars, offset);
		return new(this, lastAllocationId);
	}

	/// <summary>
	/// Find a suitable free block that can accommodate the requested size using binary search
	/// </summary>
	/// <returns>True if a suitable block was found</returns>
	private bool FindSuitableFreeBlock(int requiredSize, out FreeBlock block)
	{
		// Use binary search to find the first size >= requiredSize
		var index = FindFirstSizeIndex(requiredSize);

		// Check all sizes from the found index onwards
		for (var i = index; i < freeBlocksBySize.Count; i++) {
			var blocks = freeBlocksBySize.Values[i];
			if (blocks.Count > 0) {
				// Use the last block (best fit strategy)
				block = blocks[^1];
				return true;
			}
		}

		block = default;
		return false;
	}

	/// <summary>
	/// Find the first index in freeBlocksBySize where the key is >= requiredSize
	/// </summary>
	private int FindFirstSizeIndex(int requiredSize)
	{
		var keys = freeBlocksBySize.Keys;
		var low = 0;
		var high = keys.Count;

		// Binary search for the first key >= requiredSize
		// Using optimized arithmetic to prevent overflow and improve performance
		while (low < high) {
			var mid = low + ((high - low) >> 1); // Overflow-safe and faster than division
			if (keys[mid] >= requiredSize) {
				high = mid;
			} else {
				low = mid + 1;
			}
		}

		return low;
	}

	/// <summary>
	/// Add a free block to the size-indexed collection
	/// </summary>
	private void AddFreeBlock(FreeBlock block)
	{
		if (!freeBlocksBySize.TryGetValue(block.SizeBytes, out var sizeList)) {
			// not found, create a new list
			sizeList = [];
			freeBlocksBySize[block.SizeBytes] = sizeList;
		}

		sizeList.Add(block);
		++totalFreeBlocks;
		totalFreeBytes += block.SizeBytes;

#if DEBUG
		// fill freed block with zeroes for safety
		unsafe {
			var ptr = (void*)IntPtr.Add(basePtr, block.OffsetFromBase);
			NativeMemory.Clear(ptr, (nuint)block.SizeBytes);
		}
#endif
	}

	/// <summary>
	/// Remove a specific free block from the size-indexed collection
	/// </summary>
	private void RemoveFreeBlock(FreeBlock block)
	{
		if (freeBlocksBySize.TryGetValue(block.SizeBytes, out var sizeList)) {
			// Remove the last occurrence (which is what FindSuitableFreeBlock returns)
			// This ensures we remove the exact block we found, not just any block with same values
			for (var i = sizeList.Count - 1; i >= 0; i--) {
				if (sizeList[i].OffsetFromBase == block.OffsetFromBase && sizeList[i].SizeBytes == block.SizeBytes) {
					sizeList.RemoveAt(i);
					if (sizeList.Count == 0) {
						_ = freeBlocksBySize.Remove(block.SizeBytes);
					}

					--totalFreeBlocks;
					totalFreeBytes -= block.SizeBytes;
					return;
				}
			}
		}
	}

	/// <summary>
	/// Combine adjacent free blocks to reduce fragmentation
	/// </summary>
	private void CoalesceFreeBlocks()
	{
		if (freeBlocksBySize.Count <= 1) {
			return;
		}

		// Collect all blocks and sort by offset
		Span<FreeBlock> blocks = stackalloc FreeBlock[totalFreeBlocks];
		var index = 0;

		foreach (var blockList in freeBlocksBySize.Values) {
			foreach (var block in blockList) {
				blocks[index++] = block;
			}
		}

		if (blocks.Length <= 1) {
			return;
		}

		blocks.Sort((a, b) => a.OffsetFromBase.CompareTo(b.OffsetFromBase));

		// Rebuild with coalesced blocks
		freeBlocksBySize.Clear();
		totalFreeBlocks = 0;
		totalFreeBytes = 0;
		var currentBlock = blocks[0];

		for (var i = 1; i < blocks.Length; i++) {
			var nextBlock = blocks[i];

			if (currentBlock.OffsetFromBase + currentBlock.SizeBytes == nextBlock.OffsetFromBase) {
				// Merge blocks, but dont yet add to the collection
				currentBlock = new(currentBlock.OffsetFromBase, currentBlock.SizeBytes + nextBlock.SizeBytes);
			} else {
				// Add the current block and move to next
				AddFreeBlock(currentBlock);
				currentBlock = nextBlock;
			}
		}

		// Add the final block
		AddFreeBlock(currentBlock);
	}

	#endregion

	#region IDisposable and finalizer

	/// <summary>
	/// Gets whether this pool has been disposed
	/// </summary>
	internal bool IsDisposed { get; private set; }

	/// <summary>
	/// Disposes the pool and invalidates all associated PooledStrings
	/// </summary>
	public void Dispose()
	{
		// disposal will ensure all linked PooledStrings are invalidated
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (!IsDisposed) {
			if (disposing) {
				// Explicit disposal, clean up managed resources
				freeBlocksBySize.Clear();
				allocations.Clear();
			}

			Marshal.FreeHGlobal(basePtr); // free unmanaged memory
			IsDisposed = true;
		}
	}

	~UnmanagedStringPool()
	{
		Dispose(false);
	}

	#endregion
}

/// <summary>
/// Value type representing a string allocated from an unmanaged pool. Just a reference and an allocation ID, 12 bytes total.
/// </summary>
[System.Diagnostics.DebuggerDisplay("{ToString(),nq}")]
public readonly record struct PooledString(UnmanagedStringPool Pool, int AllocationId) : IDisposable
{
	// NOTE this struct is technically immutable, but some methods mutate the underlying pool like SetAtPosition() and Free()
	// It also implements IDisposable to call Free() automatically

	// Singleton pool for empty strings to provide consistent behavior
	private static readonly EmptyStringPool emptyPool = new();

	/// <summary>
	/// Represents an empty pooled string
	/// </summary>
	public static readonly PooledString Empty = new(emptyPool, UnmanagedStringPool.EmptyStringAllocationId);

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
	public readonly void Free()
	{
		Pool?.FreeString(AllocationId);
	}

	/// <summary>
	/// Allocate a new PooledString with the given value at the specified position. Old PooledString is unchanged.
	/// </summary>
	public readonly PooledString Insert(int pos, ReadOnlySpan<char> value)
	{
		CheckDisposed();
		if (value.IsEmpty) {
			return this;
		}

		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return pos == 0
				? Pool.Allocate(value)
				: throw new ArgumentOutOfRangeException(nameof(pos), "Cannot insert into an empty string at position other than 0");
		}

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

		var newSize = span.Length + sizeDiff * occurrences.Count;
		if (newSize < 0 || newSize > int.MaxValue) {
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
	/// Value semantics comparison
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

		return AsSpan().Equals(other.AsSpan(), StringComparison.Ordinal);
	}

	/// <summary>
	/// Convert to standard .NET string (allocates managed memory)
	/// </summary>
	public readonly override string ToString()
	{
		return AsSpan().ToString();
	}

	/// <summary>
	/// Hash code based on content
	/// </summary>
	public readonly override int GetHashCode()
	{
		if (AllocationId == UnmanagedStringPool.EmptyStringAllocationId) {
			return 0; // Empty string has a hash code of 0
		}

		if (Pool.IsDisposed) {
			return -1;
		}

		var hash = new HashCode();
		foreach (var c in AsSpan()) {
			hash.Add(c);
		}

		return hash.ToHashCode();
	}

	#endregion // public API

	/// <summary>
	/// Checks if the underlying pool is disposed before performing any operations
	/// </summary>
	private readonly void CheckDisposed()
	{
		if (Pool?.IsDisposed != false) {
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
	public void Dispose()
	{
		Free();
	}
}

/// <summary>
/// Special singleton pool that handles empty strings consistently
/// </summary>
internal sealed class EmptyStringPool : UnmanagedStringPool
{
	public EmptyStringPool() : base(1, true) // Allow growth for empty string operations
	{
	}

	internal override AllocationInfo GetAllocationInfo(int id)
	{
		if (id == EmptyStringAllocationId) {
			return new(IntPtr.Zero, 0, 0);
		}

		// For non-empty allocations, use the base class implementation
		return base.GetAllocationInfo(id);
	}

	internal override void FreeString(int id)
	{
		if (id == EmptyStringAllocationId) {
			// Empty strings don't need freeing
			return;
		}

		// For non-empty allocations, use the base class implementation
		base.FreeString(id);
	}
}
