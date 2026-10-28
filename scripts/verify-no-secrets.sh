#!/usr/bin/env bash
set -euo pipefail

# Heuristic guard for accidental credentials. This is intentionally conservative and
# complements provider secret scanning; it is not a substitute for manual review.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
exclude=(--glob '!**/bin/**' --glob '!**/obj/**' --glob '!**/node_modules/**' --glob '!**/dist/**' --glob '!**/test-results/**')
pattern='(AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35})'

if rg -n -i "${exclude[@]}" -e "$pattern" .; then
  echo 'Potential secret found in working tree.' >&2
  exit 1
fi

if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  if git log -p --all -G "$pattern" | rg -n -i -e "$pattern"; then
    echo 'Potential secret found in reachable Git history.' >&2
    exit 1
  fi
  echo 'Secret heuristic scan passed for working tree and reachable Git history.'
else
  echo 'Secret heuristic scan passed for working tree; Git history scan skipped because .git is unavailable.' >&2
fi
