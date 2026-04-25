namespace LookBusy.Demo;

using System;
using LookBusy;

public static class StringDemo
{
	public static void Main()
	{
		Console.WriteLine("=== UnmanagedStringPool Demo ===");
		Console.WriteLine();
		RunUnmanagedDemo();

		Console.WriteLine();
		Console.WriteLine("=== SegmentedStringPool Demo ===");
		Console.WriteLine();
		RunSegmentedDemo();
	}

	private static void RunUnmanagedDemo()
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
		str3.Free();
		str2.Free();

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
		Console.WriteLine(substr); // llo Be

		// Demonstrate replace operation
		var replaced = str5.Replace("Beautiful", "Wonderful");
		WriteLine(replaced); // Hello Wonderful

		Console.WriteLine($"Buffer dump: \"{pool.DumpBufferAsString()}\"");

		var last = pool.CreateEmptyString();
		for (var i = 0; i < 100; ++i) {
			var randomStr = RandomString(Random.Shared.Next(5, 150));
			var pooledStr = pool.Allocate(randomStr);
			WriteLine(pooledStr); // Print random string

			if (Random.Shared.Next(3) == 0) {
				// sometimes free the last string, this is intended to cause fragmentation
				last.Free();
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

	private static void RunSegmentedDemo()
	{
		using var pool = new SegmentedStringPool();

		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");
		var str3 = pool.Allocate("This is a longer string");
		var strX = pool.Allocate("Hello");
		Console.WriteLine($"Value semantics, does {str1} == {strX}? {str1 == strX}");

		WriteLineRef(str1); // Hello
		WriteLineRef(str2); // World
		WriteLineRef(str3); // This is a longer string

		Console.WriteLine($"Active allocations: {pool.ActiveAllocations}");
		Console.WriteLine($"Unmanaged bytes: {pool.GetTotalBytesUnmanaged()}");
		Console.WriteLine($"Slab count: {pool.SlabCount}");
		Console.WriteLine($"Arena segment count: {pool.SegmentCount}");

		// Free some strings — slab slots are returned to the fixed-size free list
		str3.Free();
		str2.Free();

		using var str4 = pool.Allocate("New");     // reuses a slab cell
		var str5 = str1.Insert(5, " Beautiful");   // allocates a new entry

		WriteLineRef(str4); // New
		WriteLineRef(str5); // Hello Beautiful

		// Large string routes to the arena tier (>128 chars by default)
		var largeStr = pool.Allocate(new string('X', 200));
		Console.WriteLine($"Large string length: {largeStr.Length} (routed to arena tier)");
		Console.WriteLine($"Arena segment count after large alloc: {pool.SegmentCount}");
		largeStr.Free();

		// Substring and replace
		var substr = str5.SubstringSpan(2, 6);
		Console.WriteLine($"Substring: {substr}"); // llo Be

		var replaced = str5.Replace("Beautiful", "Wonderful");
		WriteLineRef(replaced); // Hello Wonderful

		// Reserve capacity up front to avoid incremental slab/segment growth
		pool.Reserve(1000);

		// Stress: allocate and randomly free to show slab reuse
		var last = PooledStringRef.Empty;
		for (var i = 0; i < 100; ++i) {
			var randomStr = RandomString(Random.Shared.Next(5, 150));
			var pooledStr = pool.Allocate(randomStr);
			WriteLineRef(pooledStr);

			if (Random.Shared.Next(3) == 0) {
				last.Free();
			}

			last = pooledStr;
		}

		Console.WriteLine();
		Console.WriteLine("Final segmented pool state:");
		Console.WriteLine($"Active allocations: {pool.ActiveAllocations}");
		Console.WriteLine($"Unmanaged bytes: {pool.GetTotalBytesUnmanaged()}");
		Console.WriteLine($"Slab count: {pool.SlabCount}");
		Console.WriteLine($"Arena segment count: {pool.SegmentCount}");
	}

	private static void WriteLine(PooledString str) => Console.WriteLine(str.AsSpan());

	private static void WriteLineRef(PooledStringRef str) => Console.WriteLine(str.AsSpan());

	private static string RandomString(int length)
	{
		if (length <= 0) {
			return string.Empty;
		}

		const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
		var buffer = length <= 256 ? stackalloc char[length] : new char[length];

		for (var i = 0; i < length; ++i) {
			buffer[i] = chars[Random.Shared.Next(chars.Length)];
		}

		return new(buffer);
	}
}
