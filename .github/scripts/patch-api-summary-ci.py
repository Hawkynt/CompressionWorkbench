#!/usr/bin/env python3
from pathlib import Path

path = Path(".github/workflows/ci.yml")
text = path.read_text(encoding="utf-8")
old = '''      - name: Check package READMEs
        if: matrix.os == 'ubuntu-latest'
        uses: Hawkynt/RepositoryTemplate/package-readme@v1

      # Core tests -- always run (no external tools required).
'''
new = '''      - name: Check package READMEs
        if: matrix.os == 'ubuntu-latest'
        uses: Hawkynt/RepositoryTemplate/package-readme@b64e9f64cb808f7d0141eadff41c97e82925c14d

      # A blank final table cell in REFERENCE.md means a public/protected API member has no summary.
      # Keep this separate from the generator's broader advisory warnings (for example missing examples):
      # summaries are part of the package contract and therefore mandatory.
      - name: Require complete public API summaries
        if: matrix.os == 'ubuntu-latest'
        shell: bash
        run: |
          set -euo pipefail
          failed=0
          found=0
          while IFS= read -r file; do
            found=1
            blank=$(grep -nE '\\|[[:space:]]*\\|$' "$file" || true)
            if [[ -n "$blank" ]]; then
              echo "::error file=$file::Public API reference contains blank summary cells"
              echo "$blank"
              failed=1
            fi
          done < <(git ls-files | grep '/REFERENCE\\.md$')
          test "$found" -eq 1
          test "$failed" -eq 0

      # Core tests -- always run (no external tools required).
'''
if old in text:
    path.write_text(text.replace(old, new, 1), encoding="utf-8")
elif new not in text:
    raise SystemExit("Expected package-readme CI block not found")
