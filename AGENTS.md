# Project Overview

.NET 10.0 unmanaged string pool reducing GC pressure. The primary implementation is `SegmentedStringPool`; `UnmanagedStringPool` is the older single-block implementation, retained for reference.

## Version control (read first)

Before the first VCS command, run `jj --ignore-working-copy root`. This may be a jj repo on one machine and plain Git on another.

**IMPORTANT:** if it succeeds, use `jj` for all VCS commands, including `log`, `show`, `status` and `diff`. Do not run `git` on jj repos. The `gitStatus` snapshot in the session context is not a reason to use git.

jj has no commit hooks. Run the pre-commit checks before `jj commit`, `jj describe` (when finalising a change) and `jj squash`.
If a change touches only non-code files (`*.md`), skip the dotnet steps.

Before `jj git push` or moving a shared bookmark, run `scripts/pre-push.sh`. By default it formats the tip (newest non-empty mutable revision) with `jj fix` (`scripts/format-stdin.sh`, whitespace rules), then runs restore, build, `dotnet format --verify-no-changes` and tests on it. `--full` formats every mutable revision in `::@` and checks each in its own checkout via `jj run`. `scripts/agent-pre-push-hook.sh` runs the tip check on every push command and blocks the push on failure. Claude Code (`.claude/settings.json`) and Codex (`.codex/hooks.json`) call it as a `PreToolUse` hook. The hook checks the tip of `@`, not the bookmark being pushed, so push only the bookmark you are working on: the one on `@-`, with an empty `@` above it. In a plain Git checkout it runs the same checks on the working tree without formatting it, and refuses uncommitted changes to tracked files so the checked tree is the pushed HEAD. `git push` triggers the same hook.

Push only on explicit request. "Push this" means: if `@` is non-empty, `jj commit` it (after the pre-commit checks). Move the bookmark to `@-` with `jj bookmark set <name> -r @-`, then `jj git push --bookmark <name>`. Use the bookmark already on the stack; otherwise `main`. Never push any other bookmark. Do not use `jj git push -c`.

## Personal information

Exclude PII from every commit, commit message and bookmark name: real names, email addresses, usernames, machine paths such as `/Users/<name>/`, hostnames, tokens and credentials. Check the diff before `jj commit`, `jj describe` (finalising) and `git commit`.

## Build and Test

```bash
dotnet build
dotnet test
dotnet test --logger:"console;verbosity=detailed"
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"
```

Tests must be run with `gtimeout`.

## Code Quality

**IMPORTANT:** Run the following steps in sequence before every commit. All three must pass — do not skip or commit on failure. Stop and fix on any failure — never skip with `--no-verify`.

```bash
dotnet build
dotnet format
gtimeout 120 dotnet test
```

`scripts/pre-commit.sh` runs the same sequence (format with `--verify-no-changes`) and skips it when no .NET source or build files changed. It reads changed files from `@` in jj, otherwise from `git diff HEAD`. In a plain Git checkout, install it as the hook: `ln -sf ../../scripts/pre-commit.sh .git/hooks/pre-commit`.

## Architecture

### SegmentedStringPool (primary)

Two-tier allocator: small strings go to the slab tier; larger strings go to the arena tier.

**Slab tier** (`SegmentedSlabTier`, `SegmentedSlab`)
- Five size classes: 8, 16, 32, 64, 128 chars
- Each class is an intrusive singly-linked chain of fixed-cell slabs backed by unmanaged memory
- Bitmap tracking (1 = free); `BitOperations.TrailingZeroCount` gives O(1) allocation
- Full slabs are unlinked from the active chain; freeing a cell re-links if previously full

**Arena tier** (`SegmentedArenaTier`, `SegmentedArenaSegment`)
- Bump allocator from the tail + segregated free-list bins (keyed by Log2(blockSize)) from the head
- Free blocks embed a `SegmentedFreeBlockHeader` inline in the freed memory
- Oversized allocations get a dedicated segment; existing segments are never resized or moved

**Slot table** (`SegmentedSlotTable`, `SegmentedSlotEntry`)
- Dynamically-growing array; doubles on growth
- Intrusive free-list via generation high bit + Ptr field; generation increments on reuse to detect dangling refs

**Handle** (`PooledStringRef`)
- 16-byte readonly struct; no heap allocation
- Validated via slot generation; invalidated on disposal or explicit free

### UnmanagedStringPool (legacy)
- Single contiguous unmanaged memory block; thread-safe reads, external sync for mutations
- Auto-grows with configurable factor; finalizer cleans up unmanaged memory
- `PooledString`: 12-byte struct (pool reference + allocation ID); allocation ID 0 reserved for empty strings

## Code Style

- Test namespace: `LookBusy.Test`
- Formatting and analyzer rules enforced by `.editorconfig` and the build
- Prefer pre-increment (`++a`) over post-increment; use post-increment (`a++`) only when the pre-increment value is required (e.g. `array[a++] = 1`)
- Property getters must be trivially cheap (field read or simple arithmetic). Any getter that loops, allocates, or does non-trivial work must be a method (`GetFoo()`) so callers see the cost.

- **IMPORTANT** Every `dotnet` Bash call must set `dangerouslyDisableSandbox: true` (build, test, format, run, restore, publish, and any `gtimeout`-wrapped variants). The Claude Code sandbox blocks `dotnet` even when listed in `excludedCommands`: MSBuild's Unix-domain sockets for diagnostic IPC and worker-node communication fail under `network-inbound` deny, and the EPERM surfaces as a silent generic build failure.
