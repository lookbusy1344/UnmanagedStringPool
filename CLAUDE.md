# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## AI Assistant Guidelines

See `.github/copilot-instructions.md` for general AI assistant guidelines. Key points for Claude Code:
- Target responses at senior engineer level (15+ years experience)
- Keep responses concise with minimal code sections
- Use modern coding styles and functional programming idioms
- Avoid unnecessary apologies or excitement
- Generate brief, single-sentence commit messages

## Project Overview

This is a .NET 9.0 test project implementing an unmanaged string pool to reduce GC load. The `UnmanagedStringPool` class allocates a single block of unmanaged memory and provides string allocations as `PooledString` structs that point into the pool.

## Build and Test Commands

```bash
# Build the project
dotnet build

# Run all tests
dotnet test

# Run tests with verbose output
dotnet test --logger:"console;verbosity=detailed"

# Run a specific test class
dotnet test --filter "FullyQualifiedName~UnmanagedStringPoolTests"

# Run a specific test method
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"
```

## Code Analysis and Linting

The project has extensive analyzer configurations with strict code quality rules:

```bash
# Build with code analysis enforcement
dotnet build /p:EnforceCodeStyleInBuild=true

# Check for analyzer warnings/errors
dotnet build --no-incremental

# Format code after making changes
dotnet format

# Verify code is properly formatted without making changes
dotnet format --verify-no-changes
```

**IMPORTANT**: Always run `dotnet format` after making any code changes to ensure consistent formatting.

## Core Architecture

### UnmanagedStringPool
- Manages a single contiguous block of unmanaged memory
- Implements memory allocation, deallocation, and defragmentation
- Thread-safe for reads, requires external synchronization for mutations
- Automatic growth capability with configurable growth factor
- Finalizer ensures unmanaged memory cleanup

### PooledString
- Value type (struct) representing a string in the pool
- 12 bytes total: pool reference + allocation ID
- Full copy semantics, no heap allocation per string
- Becomes invalid if pool is disposed or string is freed
- Implements IDisposable for deterministic cleanup

### Memory Management Strategy
- 8-byte alignment for optimal memory usage
- Free block coalescing to reduce fragmentation
- Size-indexed free block tracking for efficient allocation
- Defragmentation triggered at 35% fragmentation threshold
- Allocation IDs never reused to prevent dangling references

## Test Structure

- **UnmanagedStringPoolTests.cs**: Core functionality tests
- **UnmanagedStringPoolEdgeCaseTests.cs**: Edge cases and error conditions
- **FragmentationAndMemoryTests.cs**: Memory management and fragmentation
- **PooledStringTests.cs**: String operations and manipulations
- **ConcurrentAccessTests.cs**: Thread safety validation
- **DisposalAndLifecycleTests.cs**: Disposal and lifecycle management

## Code Style

The project uses strict .editorconfig settings with:
- Tabs for indentation
- Opening braces on same line for control blocks
- File-scoped namespaces
- Extensive analyzer rules for design, performance, reliability, and usage
