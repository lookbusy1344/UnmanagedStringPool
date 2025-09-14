using System;
using System.Collections.Generic;
using LookBusy;
using Xunit;

namespace LookBusy.Test
{
    public class FragmentationCalculationTest
    {
        [Fact]
        public void FragmentationPercentage_ShouldDistinguishBetweenScatteredAndContiguousFreeSpace()
        {
            using var pool = new UnmanagedStringPool(1000);
            
            // Scenario 1: Create one large contiguous free block
            var largeString = pool.Allocate(new string('A', 200)); // 200 chars
            largeString.Free(); // Creates one large free block
            
            var fragmentationContiguous = pool.FragmentationPercentage;
            
            // Clear and reset
            pool.Clear();
            
            // Scenario 2: Create many small scattered free blocks with same total free space
            var strings = new List<PooledString>();
            for (int i = 0; i < 50; i++)
            {
                strings.Add(pool.Allocate(new string('B', 4))); // 50 * 4 = 200 chars total
            }
            
            // Free every other string to create maximum scattering
            for (int i = 1; i < strings.Count; i += 2)
            {
                strings[i].Free(); // Frees 25 strings = 100 chars
            }
            
            var fragmentationScattered = pool.FragmentationPercentage;
            
            // BUG: Current implementation shows same fragmentation for both scenarios
            // even though the scattered scenario should have MUCH higher fragmentation
            Console.WriteLine($"Contiguous free block fragmentation: {fragmentationContiguous:F2}%");
            Console.WriteLine($"Scattered free blocks fragmentation: {fragmentationScattered:F2}%");
            
            // This assertion will fail with current implementation, demonstrating the bug
            // Assert.True(fragmentationScattered > fragmentationContiguous, 
            //     "Scattered free blocks should show higher fragmentation than contiguous blocks");
            
            // With the corrected implementation:
            // Contiguous free space should have 0% fragmentation
            Assert.Equal(0.0, fragmentationContiguous, 1); // One large block = no fragmentation
            
            // Scattered free blocks should have higher fragmentation  
            Assert.True(fragmentationScattered > 0, "Scattered free blocks should show fragmentation > 0%");
            Assert.True(fragmentationScattered > fragmentationContiguous, 
                "Scattered blocks should have higher fragmentation than contiguous blocks");
            
            Console.WriteLine($"Contiguous: {fragmentationContiguous:F2}%, Scattered: {fragmentationScattered:F2}%");
        }

        [Fact]
        public void FragmentationPercentage_EdgeCases_BehaveCorrectly()
        {
            using var pool = new UnmanagedStringPool(1000);
            
            // Empty pool should have 0% fragmentation
            Assert.Equal(0.0, pool.FragmentationPercentage, 1);
            
            // Pool with only allocated strings (no free blocks) should have 0% fragmentation
            var str1 = pool.Allocate("Test1");
            var str2 = pool.Allocate("Test2");
            var str3 = pool.Allocate("Test3");
            Assert.Equal(0.0, pool.FragmentationPercentage, 1);
            
            // Pool with one free block should have 0% fragmentation 
            str1.Free();
            Assert.Equal(0.0, pool.FragmentationPercentage, 1);
            
            // Pool with multiple NON-ADJACENT free blocks should show fragmentation
            str3.Free(); // Now we have 2 separate free blocks with str2 in between
            var fragmentation = pool.FragmentationPercentage;
            Console.WriteLine($"Fragmentation with 2 non-adjacent blocks: {fragmentation:F2}%");
            Assert.True(fragmentation > 0, "Multiple non-adjacent free blocks should show fragmentation > 0");
            
            // After defragmentation, fragmentation should be 0
            pool.DefragmentAndGrowPool(0);
            Assert.Equal(0.0, pool.FragmentationPercentage, 1);
        }
    }
}