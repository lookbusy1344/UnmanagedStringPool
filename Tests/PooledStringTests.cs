namespace LookBusy.Test;

using System;
using System.Collections.Generic;
using LookBusy;
using Xunit;

public sealed class PooledStringTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public PooledStringTests() => pool = new(2048);

	public void Dispose()
	{
		pool?.Dispose();
		GC.SuppressFinalize(this);
	}

	#region String Operations Tests

	[Fact]
	public void Insert_AtBeginning_WorksCorrectly()
	{
		var original = pool.Allocate("World");
		var result = original.Insert(0, "Hello ");

		Assert.Equal("Hello World", result.ToString());
		Assert.Equal("World", original.ToString()); // Original unchanged
	}

	[Fact]
	public void Insert_AtEnd_WorksCorrectly()
	{
		var original = pool.Allocate("Hello");
		var result = original.Insert(5, " World");

		Assert.Equal("Hello World", result.ToString());
		Assert.Equal("Hello", original.ToString());
	}

	[Fact]
	public void Insert_InMiddle_WorksCorrectly()
	{
		var original = pool.Allocate("HelloWorld");
		var result = original.Insert(5, " ");

		Assert.Equal("Hello World", result.ToString());
	}

	[Fact]
	public void Insert_EmptyValue_ReturnsOriginal()
	{
		var original = pool.Allocate("Hello");
		var result = original.Insert(2, "");

		Assert.Equal(original.ToString(), result.ToString());
	}

	[Fact]
	public void Insert_IntoEmptyString_WorksCorrectly()
	{
		var empty = pool.CreateEmptyString();
		var result = empty.Insert(0, "Hello");

		Assert.Equal("Hello", result.ToString());
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(6)]
	public void Insert_InvalidPosition_ThrowsArgumentOutOfRangeException(int position)
	{
		var str = pool.Allocate("Hello");

		_ = Assert.Throws<ArgumentOutOfRangeException>(() => str.Insert(position, "test"));
	}

	[Fact]
	public void Insert_IntoEmptyStringInvalidPosition_ThrowsArgumentOutOfRangeException()
	{
		var empty = pool.CreateEmptyString();

		_ = Assert.Throws<ArgumentOutOfRangeException>(() => empty.Insert(1, "test"));
	}

	[Fact]
	public void Insert_IntoAllocatedEmptyString_WorksCorrectly()
	{
		var empty = pool.Allocate("");
		var result = empty.Insert(0, "World");

		Assert.Equal("World", result.ToString());
	}

	[Fact]
	public void Insert_AppendToString_WorksCorrectly()
	{
		var str = pool.Allocate("Hello");
		var result = str.Insert(str.Length, " World");

		Assert.Equal("Hello World", result.ToString());
	}

	[Fact]
	public void Insert_AppendToEmptyString_WorksCorrectly()
	{
		var empty = pool.CreateEmptyString();
		var result = empty.Insert(empty.Length, "Appended");

		Assert.Equal("Appended", result.ToString());
	}

	#endregion

	#region Replace Tests

	[Fact]
	public void Replace_SimpleReplacement_WorksCorrectly()
	{
		var original = pool.Allocate("Hello World");
		var result = original.Replace("World", "Universe");

		Assert.Equal("Hello Universe", result.ToString());
	}

	[Fact]
	public void Replace_MultipleOccurrences_ReplacesAll()
	{
		var original = pool.Allocate("foo bar foo baz foo");
		var result = original.Replace("foo", "test");

		Assert.Equal("test bar test baz test", result.ToString());
	}

	[Fact]
	public void Replace_NotFound_ReturnsOriginal()
	{
		var original = pool.Allocate("Hello World");
		var result = original.Replace("xyz", "abc");

		Assert.Equal("Hello World", result.ToString());
	}

	[Fact]
	public void Replace_EmptyOldValue_ReturnsOriginal()
	{
		var original = pool.Allocate("Hello World");
		var result = original.Replace("", "test");

		Assert.Equal("Hello World", result.ToString());
	}

	[Fact]
	public void Replace_WithEmptyString_RemovesOccurrences()
	{
		var original = pool.Allocate("Hello World Hello");
		var result = original.Replace("Hello", "");

		Assert.Equal(" World ", result.ToString());
	}

	[Fact]
	public void Replace_ShorterReplacement_WorksCorrectly()
	{
		var original = pool.Allocate("Hello Beautiful World");
		var result = original.Replace("Beautiful", "Big");

		Assert.Equal("Hello Big World", result.ToString());
	}

	[Fact]
	public void Replace_LongerReplacement_WorksCorrectly()
	{
		var original = pool.Allocate("Hello Big World");
		var result = original.Replace("Big", "Beautiful");

		Assert.Equal("Hello Beautiful World", result.ToString());
	}

	[Fact]
	public void Replace_OverlappingPattern_HandlesCorrectly()
	{
		var original = pool.Allocate("aaaaaa");
		var result = original.Replace("aa", "b");

		Assert.Equal("bbb", result.ToString());
	}

	[Fact]
	public void Replace_EntireString_WorksCorrectly()
	{
		var original = pool.Allocate("Hello");
		var result = original.Replace("Hello", "Goodbye");

		Assert.Equal("Goodbye", result.ToString());
	}

	#endregion


	#region Search Operations Tests

	[Fact]
	public void IndexOf_Found_ReturnsCorrectIndex()
	{
		var str = pool.Allocate("Hello World Hello");

		Assert.Equal(0, str.IndexOf("Hello"));
		Assert.Equal(6, str.IndexOf("World"));
		Assert.Equal(0, str.IndexOf("Hello".AsSpan()));
	}

	[Fact]
	public void IndexOf_NotFound_ReturnsMinusOne()
	{
		var str = pool.Allocate("Hello World");

		Assert.Equal(-1, str.IndexOf("xyz"));
	}

	[Fact]
	public void IndexOf_EmptyString_ReturnsZero()
	{
		var str = pool.Allocate("Hello");

		Assert.Equal(0, str.IndexOf(""));
	}

	[Fact]
	public void IndexOf_InEmptyString_ReturnsCorrectly()
	{
		var empty = pool.CreateEmptyString();

		Assert.Equal(0, empty.IndexOf(""));
		Assert.Equal(-1, empty.IndexOf("test"));
	}

	[Fact]
	public void LastIndexOf_Found_ReturnsCorrectIndex()
	{
		var str = pool.Allocate("Hello World Hello");

		Assert.Equal(12, str.LastIndexOf("Hello"));
		Assert.Equal(6, str.LastIndexOf("World"));
	}

	[Fact]
	public void LastIndexOf_NotFound_ReturnsMinusOne()
	{
		var str = pool.Allocate("Hello World");

		Assert.Equal(-1, str.LastIndexOf("xyz"));
	}

	[Theory]
	[InlineData(StringComparison.Ordinal)]
	[InlineData(StringComparison.OrdinalIgnoreCase)]
	public void IndexOf_WithComparison_WorksCorrectly(StringComparison comparison)
	{
		var str = pool.Allocate("Hello World");

		var result = str.IndexOf("HELLO", comparison);

		if (comparison == StringComparison.OrdinalIgnoreCase) {
			Assert.Equal(0, result);
		} else {
			Assert.Equal(-1, result);
		}
	}

	#endregion

	#region Contains, StartsWith, EndsWith Tests

	[Fact]
	public void Contains_Found_ReturnsTrue()
	{
		var str = pool.Allocate("Hello World");

		Assert.True(str.Contains("World"));
		Assert.True(str.Contains("Hello"));
		Assert.True(str.Contains("llo W"));
	}

	[Fact]
	public void Contains_NotFound_ReturnsFalse()
	{
		var str = pool.Allocate("Hello World");

		Assert.False(str.Contains("xyz"));
	}

	[Fact]
	public void StartsWith_Correct_ReturnsTrue()
	{
		var str = pool.Allocate("Hello World");

		Assert.True(str.StartsWith("Hello"));
		Assert.True(str.StartsWith(""));
	}

	[Fact]
	public void StartsWith_Incorrect_ReturnsFalse()
	{
		var str = pool.Allocate("Hello World");

		Assert.False(str.StartsWith("World"));
		Assert.False(str.StartsWith("Hi"));
	}

	[Fact]
	public void EndsWith_Correct_ReturnsTrue()
	{
		var str = pool.Allocate("Hello World");

		Assert.True(str.EndsWith("World"));
		Assert.True(str.EndsWith(""));
	}

	[Fact]
	public void EndsWith_Incorrect_ReturnsFalse()
	{
		var str = pool.Allocate("Hello World");

		Assert.False(str.EndsWith("Hello"));
		Assert.False(str.EndsWith("Earth"));
	}

	[Fact]
	public void StringOperations_WithEmptyString_WorkCorrectly()
	{
		var empty = pool.CreateEmptyString();

		Assert.True(empty.Contains(""));
		Assert.True(empty.StartsWith(""));
		Assert.True(empty.EndsWith(""));
		Assert.False(empty.Contains("test"));
		Assert.False(empty.StartsWith("test"));
		Assert.False(empty.EndsWith("test"));
	}

	#endregion

	#region SubstringSpan Tests

	[Fact]
	public void SubstringSpan_ValidRange_ReturnsCorrectSpan()
	{
		var str = pool.Allocate("Hello World");
		var span = str.SubstringSpan(6, 5);

		Assert.Equal("World", span.ToString());
	}

	[Fact]
	public void SubstringSpan_FromBeginning_WorksCorrectly()
	{
		var str = pool.Allocate("Hello World");
		var span = str.SubstringSpan(0, 5);

		Assert.Equal("Hello", span.ToString());
	}

	[Fact]
	public void SubstringSpan_ToEnd_WorksCorrectly()
	{
		var str = pool.Allocate("Hello World");
		var span = str.SubstringSpan(6, 5);

		Assert.Equal("World", span.ToString());
	}

	[Fact]
	public void SubstringSpan_ZeroLength_ReturnsEmptySpan()
	{
		var str = pool.Allocate("Hello World");
		var span = str.SubstringSpan(5, 0);

		Assert.True(span.IsEmpty);
	}

	[Theory]
	[InlineData(-1, 5)]
	[InlineData(12, 1)]
	[InlineData(0, 12)]
	[InlineData(5, 7)]
	public void SubstringSpan_InvalidRange_ThrowsArgumentOutOfRangeException(int start, int length)
	{
		var str = pool.Allocate("Hello World");

		_ = Assert.Throws<ArgumentOutOfRangeException>(() => str.SubstringSpan(start, length));
	}

	#endregion

	#region AsSpan Tests

	[Fact]
	public void AsSpan_ValidString_ReturnsCorrectSpan()
	{
		var str = pool.Allocate("Hello World");
		var span = str.AsSpan();

		Assert.Equal("Hello World", span.ToString());
		Assert.Equal(11, span.Length);
	}

	[Fact]
	public void AsSpan_EmptyString_ReturnsEmptySpan()
	{
		var empty = pool.CreateEmptyString();
		var span = empty.AsSpan();

		Assert.True(span.IsEmpty);
		Assert.Equal(0, span.Length);
	}

	[Fact]
	public void AsSpan_AfterFree_ThrowsException()
	{
		var str = pool.Allocate("Hello");
		str.Free();

		_ = Assert.Throws<ArgumentException>(() => str.AsSpan());
	}

	#endregion

	#region Value Semantics and Equality Tests

	[Fact]
	public void Equals_SameContent_ReturnsTrue()
	{
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("Hello");

		Assert.True(str1.Equals(str2));
		Assert.True(str1 == str2);
	}

	[Fact]
	public void Equals_DifferentContent_ReturnsFalse()
	{
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");

		Assert.False(str1.Equals(str2));
		Assert.False(str1 == str2);
	}

	[Fact]
	public void Equals_EmptyStrings_ReturnsTrue()
	{
		using var pool = new UnmanagedStringPool(1024);
		var empty1 = pool.CreateEmptyString();
		var empty2 = pool.CreateEmptyString();

		Assert.True(empty1.Equals(empty2));
		Assert.True(empty1 == empty2);
	}

	[Fact]
	public void Equals_EmptyWithAllocated_WorksCorrectly()
	{
		var empty = pool.CreateEmptyString();
		var emptyAllocated = pool.Allocate("");
		var nonEmpty = pool.Allocate("Hello");

		Assert.True(empty.Equals(emptyAllocated));
		Assert.False(empty.Equals(nonEmpty));
	}

	[Fact]
	public void GetHashCode_SameContent_ReturnsSameHashCode()
	{
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("Hello");

		Assert.Equal(str1.GetHashCode(), str2.GetHashCode());
	}

	[Fact]
	public void GetHashCode_DifferentContent_ReturnsDifferentHashCode()
	{
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");

		// Hash codes should typically be different (though not guaranteed)
		Assert.NotEqual(str1.GetHashCode(), str2.GetHashCode());
	}

	[Fact]
	public void GetHashCode_RandomStrings_MostHaveDifferentHashes()
	{
		const int stringCount = 20;
		var random = new Random(42); // Fixed seed for reproducibility
		var strings = new List<PooledString>(stringCount);
		var hashes = new HashSet<int>();

		// Generate random strings of varying lengths
		for (var i = 0; i < stringCount; i++) {
			var length = random.Next(5, 30);
			var chars = new char[length];
			for (var j = 0; j < length; j++) {
				// Generate random printable ASCII characters
				chars[j] = (char)random.Next(32, 127);
			}

			strings.Add(pool.Allocate(new string(chars)));
		}

		// Collect unique hash codes
		foreach (var str in strings) {
			_ = hashes.Add(str.GetHashCode());
		}

		// Expect at least 80% unique hashes (allowing for some collisions)
		// With 20 strings and a good hash function, we'd expect 18-20 unique hashes
		var minUniqueHashes = (int)(stringCount * 0.8);
		Assert.True(hashes.Count >= minUniqueHashes,
			$"Expected at least {minUniqueHashes} unique hashes out of {stringCount} strings, but got {hashes.Count}");

		// Also verify that identical strings still produce identical hashes
		var duplicate = pool.Allocate(strings[0].ToString());
		Assert.Equal(strings[0].GetHashCode(), duplicate.GetHashCode());
	}

	[Fact]
	public void GetHashCode_EmptyString_ReturnsZero()
	{
		var empty = pool.CreateEmptyString();

		Assert.Equal(0, empty.GetHashCode());
	}

	[Fact]
	public void GetHashCode_IdenticalStrings_AlwaysReturnSameHash()
	{
		// Test with various string patterns
		var testStrings = new[] {
			"", "a", "Hello World", "The quick brown fox jumps over the lazy dog", "12345678901234567890", "Special chars: !@#$%^&*()",
			"Unicode: 你好世界 🌍", "  spaces  at  various  positions  ", "\t\n\r", new string('x', 100)
		};

		foreach (var testString in testStrings) {
			var str1 = pool.Allocate(testString);
			var str2 = pool.Allocate(testString);
			var str3 = pool.Allocate(testString);

			var hash1 = str1.GetHashCode();
			var hash2 = str2.GetHashCode();
			var hash3 = str3.GetHashCode();

			Assert.Equal(hash1, hash2);
			Assert.Equal(hash2, hash3);
			Assert.Equal(hash1, hash3);
		}
	}

	[Fact]
	public void GetHashCode_RandomIdenticalStrings_ConsistentHashes()
	{
		var random = new Random(123);
		const int iterations = 50;

		for (var i = 0; i < iterations; i++) {
			// Generate a random string
			var length = random.Next(0, 100);
			var chars = new char[length];
			for (var j = 0; j < length; j++) {
				chars[j] = (char)random.Next(32, 127);
			}

			var testString = new string(chars);

			// Allocate the same string multiple times
			var str1 = pool.Allocate(testString);
			var str2 = pool.Allocate(testString);
			var str3 = pool.Allocate(testString);

			// All should have identical hash codes
			Assert.Equal(str1.GetHashCode(), str2.GetHashCode());
			Assert.Equal(str2.GetHashCode(), str3.GetHashCode());
		}
	}

	[Fact]
	public void GetHashCode_SameStringDifferentPools_SameHash()
	{
		using var pool2 = new UnmanagedStringPool(1024);
		var random = new Random(456);

		for (var i = 0; i < 20; i++) {
			// Generate random string
			var length = random.Next(5, 50);
			var chars = new char[length];
			for (var j = 0; j < length; j++) {
				chars[j] = (char)random.Next(65, 91); // A-Z
			}

			var testString = new string(chars);

			// Allocate in different pools
			var strPool1 = pool.Allocate(testString);
			var strPool2 = pool2.Allocate(testString);

			// Hash codes must be identical regardless of pool
			Assert.Equal(strPool1.GetHashCode(), strPool2.GetHashCode());
		}
	}

	[Fact]
	public void GetHashCode_RepeatedCallsSameInstance_ConsistentHash()
	{
		var str = pool.Allocate("Test String");

		// Call GetHashCode multiple times on the same instance
		var hash1 = str.GetHashCode();
		var hash2 = str.GetHashCode();
		var hash3 = str.GetHashCode();
		var hash4 = str.GetHashCode();
		var hash5 = str.GetHashCode();

		// All calls must return the same value
		Assert.Equal(hash1, hash2);
		Assert.Equal(hash1, hash3);
		Assert.Equal(hash1, hash4);
		Assert.Equal(hash1, hash5);
	}

	[Fact]
	public void GetHashCode_AfterPoolOperations_RemainsConsistent()
	{
		var str1 = pool.Allocate("Persistent String");
		var originalHash = str1.GetHashCode();

		// Perform various pool operations
		var temp1 = pool.Allocate("Temporary 1");
		var temp2 = pool.Allocate("Temporary 2");
		temp1.Dispose();
		var temp3 = pool.Allocate("Temporary 3");
		temp2.Dispose();
		pool.DefragmentAndGrowPool(0);
		var temp4 = pool.Allocate("Temporary 4");

		// Original string's hash should remain the same
		Assert.Equal(originalHash, str1.GetHashCode());

		// Allocate identical string after operations
		var str2 = pool.Allocate("Persistent String");
		Assert.Equal(originalHash, str2.GetHashCode());
	}

	#endregion

	#region Length and IsEmpty Tests

	[Fact]
	public void Length_CorrectlyReturnsStringLength()
	{
		var str = pool.Allocate("Hello World");

		Assert.Equal(11, str.Length);
	}

	[Fact]
	public void Length_EmptyString_ReturnsZero()
	{
		var empty = pool.CreateEmptyString();
		var emptyAllocated = pool.Allocate("");

		Assert.Equal(0, empty.Length);
		Assert.Equal(0, emptyAllocated.Length);
	}

	[Fact]
	public void IsEmpty_EmptyString_ReturnsTrue()
	{
		var empty = pool.CreateEmptyString();
		var emptyAllocated = pool.Allocate("");

		Assert.True(empty.IsEmpty);
		Assert.True(emptyAllocated.IsEmpty);
	}

	[Fact]
	public void IsEmpty_NonEmptyString_ReturnsFalse()
	{
		var str = pool.Allocate("Hello");

		Assert.False(str.IsEmpty);
	}

	#endregion

	#region ToString Tests

	[Fact]
	public void ToString_ValidString_ReturnsCorrectString()
	{
		var str = pool.Allocate("Hello World");

		Assert.Equal("Hello World", str.ToString());
	}

	[Fact]
	public void ToString_EmptyString_ReturnsEmptyString()
	{
		var empty = pool.CreateEmptyString();

		Assert.Equal("", empty.ToString());
	}

	[Fact]
	public void ToString_WithSpecialCharacters_WorksCorrectly()
	{
		var str = pool.Allocate("Hello\nWorld\t!");

		Assert.Equal("Hello\nWorld\t!", str.ToString());
	}

	[Fact]
	public void ToString_WithUnicodeCharacters_WorksCorrectly()
	{
		var str = pool.Allocate("Hello 🌍 World");

		Assert.Equal("Hello 🌍 World", str.ToString());
	}

	#endregion

	#region Complex Operation Chains

	[Fact]
	public void ChainedOperations_WorkCorrectly()
	{
		var original = pool.Allocate("Hello World");
		var result = original
			.Replace("World", "Beautiful World")
			.Insert(0, "Say ")
			.Replace("Say Hello", "Greet the");

		Assert.Equal("Greet the Beautiful World", result.ToString());
		Assert.Equal("Hello World", original.ToString()); // Original unchanged
	}

	[Fact]
	public void MultipleReplace_WorksCorrectly()
	{
		var original = pool.Allocate("The quick brown fox jumps over the lazy dog");
		var result = original
			.Replace("quick", "fast")
			.Replace("brown", "red")
			.Replace("lazy", "sleepy");

		Assert.Equal("The fast red fox jumps over the sleepy dog", result.ToString());
	}

	#endregion
}
