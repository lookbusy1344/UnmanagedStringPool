# CLAUDE.md

## Project Overview

.NET 10.0 unmanaged string pool reducing GC load. `UnmanagedStringPool` allocates a contiguous block of unmanaged memory; strings are `PooledString` structs pointing into it.

## Build and Test

```bash
dotnet build
dotnet test
dotnet test --logger:"console;verbosity=detailed"
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"
```

Tests must be run with `gtimeout`.

## Code Quality

Always run `dotnet format` after code changes. Use `/pre-commit` for the full pre-commit procedure (format + analyzer check + tests).

```bash
dotnet format
dotnet build /p:EnforceCodeStyleInBuild=true
```

## Committing

Before every commit, run the `pre-commit` skill.

## Architecture

### UnmanagedStringPool
- Single contiguous unmanaged memory block; thread-safe reads, external sync for mutations
- Auto-grows with configurable factor; finalizer cleans up unmanaged memory

### PooledString
- 12-byte struct (pool reference + allocation ID); full copy semantics, no heap allocation
- Allocation ID 0 reserved for empty strings; IDs never reused (prevents dangling refs)
- Invalidated on pool disposal or explicit free

### Memory Model
- 8-byte aligned; free block coalescing; size-indexed free list
- Defrag triggers at 35% fragmentation threshold

## Code Style

- Test namespace: `LookBusy.Test`
- Formatting and analyzer rules enforced by `.editorconfig` and the build
