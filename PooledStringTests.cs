namespace Playground.Tests;

using System;
using System.Collections.Generic;
using Xunit;

public sealed class PooledStringTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public PooledStringTests()
	{
		pool = new UnmanagedStringPool(2048);
	}

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
		var empty = PooledString.Empty;
		var result = empty.Insert(0, "Hello");

		Assert.Equal("Hello", result.ToString());
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(6)]
	public void Insert_InvalidPosition_ThrowsArgumentOutOfRangeException(int position)
	{
		var str = pool.Allocate("Hello");

		Assert.Throws<ArgumentOutOfRangeException>(() => str.Insert(position, "test"));
	}

	[Fact]
	public void Insert_IntoEmptyStringInvalidPosition_ThrowsArgumentOutOfRangeException()
	{
		var empty = PooledString.Empty;

		Assert.Throws<ArgumentOutOfRangeException>(() => empty.Insert(1, "test"));
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
		var empty = PooledString.Empty;

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
		var empty = PooledString.Empty;

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

		Assert.Throws<ArgumentOutOfRangeException>(() => str.SubstringSpan(start, length));
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
		var empty = PooledString.Empty;
		var span = empty.AsSpan();

		Assert.True(span.IsEmpty);
		Assert.Equal(0, span.Length);
	}

	[Fact]
	public void AsSpan_AfterFree_ThrowsException()
	{
		var str = pool.Allocate("Hello");
		str.Free();

		Assert.Throws<ArgumentException>(() => str.AsSpan());
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
		var empty1 = PooledString.Empty;
		var empty2 = PooledString.Empty;

		Assert.True(empty1.Equals(empty2));
		Assert.True(empty1 == empty2);
	}

	[Fact]
	public void Equals_EmptyWithAllocated_WorksCorrectly()
	{
		var empty = PooledString.Empty;
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
	public void GetHashCode_EmptyString_ReturnsZero()
	{
		var empty = PooledString.Empty;

		Assert.Equal(0, empty.GetHashCode());
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
		var empty = PooledString.Empty;
		var emptyAllocated = pool.Allocate("");

		Assert.Equal(0, empty.Length);
		Assert.Equal(0, emptyAllocated.Length);
	}

	[Fact]
	public void IsEmpty_EmptyString_ReturnsTrue()
	{
		var empty = PooledString.Empty;
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
		var empty = PooledString.Empty;

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
