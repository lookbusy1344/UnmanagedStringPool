---
name: pre-commit
description: Use when about to run git commit, jj commit, jj describe (finalising) or jj squash in this project - runs the required verification sequence first
---

# Pre-Commit Checklist

Run these in order before `git commit`, or in a jj repo (jj has no commit hooks) before `jj commit`, `jj describe` (finalising) and `jj squash`. All must pass cleanly.

```bash
dotnet build --configuration Debug --no-restore
dotnet format UnmanagedStringPool.sln
gtimeout 120 dotnet test --no-restore
```

`scripts/pre-commit.sh` runs the same sequence and skips it for documentation-only changes. It reads changed files from jj when available, otherwise from Git.

**Tests must pass** — do not commit with failing tests. If a test fails unexpectedly, investigate before touching the test.
