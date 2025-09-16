# UnmanagedStringPool Demo

This directory contains a demonstration application showing practical usage of the UnmanagedStringPool library.

## Overview

The demo application (`Demo.cs`) provides a hands-on example of:
- Creating and managing a string pool
- Allocating and freeing strings
- String manipulation operations
- Pool memory statistics and fragmentation monitoring
- Pool growth and defragmentation

## Running the Demo

```bash
# From the Demo directory
dotnet run

# Or from the project root
dotnet run --project Demo/StringPoolDemo.csproj
```

## Demo Features

### Basic Operations
- String allocation from the pool
- Value semantics demonstration (struct equality)
- String freeing and memory reuse
- Using statement integration with disposable strings

### String Manipulation
- String insertion operations
- Replacement and trimming
- Substring extraction
- Case conversion

### Memory Management
- Real-time fragmentation monitoring
- Free space tracking
- Active allocation counting
- Forced defragmentation and pool growth

### Performance Comparison
- Timing comparisons between pooled and regular strings
- Memory allocation stress testing
- GC pressure demonstration

## Sample Output

```
Value semantics, does Hello == Hello? False
Hello
World
This is a longer string
Free space before: 3968
End block space: 3968
Active allocations: 4
Fragmentation: 0.00%
...
After pool growth:
Free space after: 8064
End block space: 8064
Active allocations: 4
Fragmentation: 0.00%
```

## Key Learning Points

1. **Struct Semantics**: PooledString instances are value types with reference equality semantics
2. **Memory Efficiency**: See how freed memory is immediately reused
3. **Fragmentation**: Observe fragmentation patterns and defragmentation in action
4. **Growth**: Watch the pool automatically grow when needed
5. **Disposal**: Proper resource cleanup with using statements

## Customization

Modify the demo to experiment with:
- Different initial pool sizes
- Various string allocation patterns
- Concurrent access scenarios (with proper synchronization)
- Large-scale string operations
- Custom fragmentation thresholds