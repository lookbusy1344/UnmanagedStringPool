using LookBusy;
using Xunit;

namespace LookBusy.Test;

/// <summary>
/// Tests for handling null Pool references in PooledString
/// </summary>
public sealed class NullPoolTests
{
    [Fact]
    public void GetHashCode_WithNullPool_ShouldNotThrowNullReferenceException()
    {
        // Create a PooledString with null pool (this is possible via reflection or unsafe code)
        var pooledString = new PooledString(null!, 1);
        
        // This should not throw NullReferenceException
        var exception = Record.Exception(() => pooledString.GetHashCode());
        
        // Should handle gracefully, not crash with NullReferenceException
        Assert.True(exception == null || exception is not NullReferenceException);
    }

    [Fact]
    public void AsSpan_WithNullPool_ShouldThrowObjectDisposedException()
    {
        var pooledString = new PooledString(null!, 1);
        
        // Should throw ObjectDisposedException, not NullReferenceException
        var exception = Record.Exception(() => pooledString.AsSpan());
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public void GetHashCode_OptimizedVersion_ProducesSameResults()
    {
        using var pool = new UnmanagedStringPool(1000);
        
        var testStrings = new[] {
            "",
            "a",
            "short",
            "medium length string",
            new string('x', 100), // Longer than 64 chars to test fragment hashing
            new string('y', 1000) // Very long string
        };

        foreach (var testStr in testStrings) {
            var pooledStr1 = pool.Allocate(testStr);
            var pooledStr2 = pool.Allocate(testStr);

            // Same content should produce same hash
            Assert.Equal(pooledStr1.GetHashCode(), pooledStr2.GetHashCode());
            
            // Hash should be consistent across multiple calls
            Assert.Equal(pooledStr1.GetHashCode(), pooledStr1.GetHashCode());
        }
    }
}