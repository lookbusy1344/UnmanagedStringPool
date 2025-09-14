namespace LookBusy;

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
		public override unsafe string ToString() => new((char*)Pointer, 0, LengthChars);
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

	protected virtual void Dispose(bool disposing)
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

	~UnmanagedStringPool() => Dispose(false);

	#endregion
}

