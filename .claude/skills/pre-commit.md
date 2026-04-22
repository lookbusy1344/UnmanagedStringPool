---
name: pre-commit
description: Run the full pre-commit quality check for this project (format, analyzers, tests)
---

Run the following commands in sequence, stopping and reporting on any failure:

**IMPORTANT:** Run the following steps in sequence before every commit. All three must pass — do not skip or commit on failure. Stop and fix on any failure — never skip with `--no-verify`.

```bash
dotnet build
dotnet format
gtimeout 120 dotnet test
```
