---
name: pre-commit
description: Run the full pre-commit quality check for this project (format, analyzers, tests)
---

Run the following commands in sequence, stopping and reporting on any failure:

1. **Format** — apply consistent formatting:
   ```bash
   dotnet format
   ```

2. **Analyzer check** — enforce code style and analysis rules:
   ```bash
   dotnet build /p:EnforceCodeStyleInBuild=true --no-incremental
   ```

3. **Tests** — confirm nothing is broken (use gtimeout since coreutils is installed):
   ```bash
   gtimeout 120 dotnet test
   ```

Report pass/fail for each step. If any step fails, stop and show the relevant output. Do not proceed to commit until all three pass.
