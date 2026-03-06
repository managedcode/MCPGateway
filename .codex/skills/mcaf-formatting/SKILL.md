---
name: mcaf-formatting
description: "Format code and keep style consistent using the canonical formatting command from `AGENTS.md`. Use after implementation or when style drift causes noisy diffs, and keep formatting changes intentional and separately reviewable."
compatibility: "Requires the repository formatter and linter tools; uses commands from AGENTS.md."
---

# MCAF: Formatting

## Outputs

- formatted code changes consistent with repo style
- evidence of the formatting command and any follow-up verification

## Workflow

1. Use the canonical `format` command from `AGENTS.md`.
2. Run the formatter on the smallest meaningful scope when the tool supports it.
3. Review the diff:
   - ensure the changes are formatting-only
   - if many unrelated files were touched, split the change or confirm that broad formatting was intended
4. Verify using the repo order from `AGENTS.md`:
   - for formatting-only changes, run the smallest meaningful verification the repo requires
   - for formatting as part of a behavior change, keep the normal pipeline order
5. If `format` or analyzers are missing or flaky:
   - fix `AGENTS.md` first so the command is real and repeatable
   - rerun formatting after that
6. Report what was run and what changed.

## Guardrails

- Do not hide unrelated refactors inside formatting.
- Keep formatting changes and behavior changes reviewable.
