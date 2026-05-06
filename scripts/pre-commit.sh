#!/usr/bin/env bash

# Call from .git/hooks/pre-commit:
#   if [ ! -x "scripts/pre-commit.sh" ]; then
#       echo "Missing executable scripts/pre-commit.sh" >&2
#       exit 1
#   fi
#
#   ./scripts/pre-commit.sh

set -euo pipefail

readonly TEST_TIMEOUT_SECONDS="120"

# Keep docs-only commits fast by skipping the .NET validation pipeline.
is_documentation_file() {
  local path="$1"

  [[ "$path" == *.md ]] \
    || [[ "$path" == docs/* ]] \
    || [[ "$(basename "$path")" == README* ]]
}

requires_dotnet_checks() {
  local path="$1"

  [[ "$path" == *.cs ]] \
    || [[ "$path" == *.csproj ]] \
    || [[ "$path" == *.sln ]] \
    || [[ "$path" == *.editorconfig ]] \
    || [[ "$path" == *.props ]] \
    || [[ "$path" == *.targets ]]
}

main() {
  local staged_files=()
  local staged_file

  # Bash 3 on macOS does not provide mapfile, so collect staged paths manually.
  while IFS= read -r staged_file; do
    staged_files+=("$staged_file")
  done < <(git diff --cached --name-only --diff-filter=ACMR)

  if [[ "${#staged_files[@]}" -eq 0 ]]; then
    echo "No staged files found."
    exit 0
  fi

  local has_non_documentation_files="false"
  local has_dotnet_files="false"

  for staged_file in "${staged_files[@]}"; do
    if ! is_documentation_file "$staged_file"; then
      has_non_documentation_files="true"
    fi

    if requires_dotnet_checks "$staged_file"; then
      has_dotnet_files="true"
    fi
  done

  if [[ "$has_non_documentation_files" == "false" ]]; then
    echo "Documentation-only commit detected. Skipping .NET checks."
    exit 0
  fi

  # Non-doc changes still skip the expensive checks unless .NET-relevant files are staged.
  if [[ "$has_dotnet_files" == "false" ]]; then
    echo "No staged .NET source or build files detected. Skipping .NET checks."
    exit 0
  fi

  echo "Running pre-commit checks for staged .NET files..."
  dotnet build
  dotnet format --verify-no-changes
  gtimeout "$TEST_TIMEOUT_SECONDS" dotnet test
}

main "$@"
