#!/usr/bin/env bash

# Call from .git/hooks/pre-commit:
#   if [ ! -x "scripts/pre-commit.sh" ]; then
#       echo "Missing executable scripts/pre-commit.sh" >&2
#       exit 1
#   fi
#
#   exec ./scripts/pre-commit.sh

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
  local changed_files=()
  local changed_file

  # Bash 3 on macOS does not provide mapfile, so collect paths manually.
  # Use git diff HEAD (staged + unstaged) so a dirty working tree can't slip
  # past the trigger when only unrelated files are staged.
  while IFS= read -r changed_file; do
    changed_files+=("$changed_file")
  done < <(git diff HEAD --name-only --diff-filter=ACMR)

  if [[ "${#changed_files[@]}" -eq 0 ]]; then
    echo "No changed files found."
    exit 0
  fi

  local has_non_documentation_files="false"
  local has_dotnet_files="false"

  for changed_file in "${changed_files[@]}"; do
    if ! is_documentation_file "$changed_file"; then
      has_non_documentation_files="true"
    fi

    if requires_dotnet_checks "$changed_file"; then
      has_dotnet_files="true"
    fi
  done

  if [[ "$has_non_documentation_files" == "false" ]]; then
    echo "Documentation-only changes detected. Skipping .NET checks."
    exit 0
  fi

  # Non-doc changes still skip the expensive checks unless .NET-relevant files changed.
  if [[ "$has_dotnet_files" == "false" ]]; then
    echo "No .NET source or build files changed. Skipping .NET checks."
    exit 0
  fi

  echo "Running pre-commit checks for staged .NET files..."
  dotnet build
  dotnet format --verify-no-changes
  gtimeout "$TEST_TIMEOUT_SECONDS" dotnet test
}

main "$@"
