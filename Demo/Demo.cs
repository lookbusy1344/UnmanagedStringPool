namespace LookBusy.Demo;

using System;
using LookBusy;

public static class StringDemo
{
	public static void Main()
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

	private static void WriteLine(PooledString str) => Console.Out.WriteLine(str.AsSpan());

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
