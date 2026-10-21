# Liro — Coding Guidelines

- Prioritize simplicity, readability, and maintainability.
- Follow KISS, DRY, and YAGNI principles.
- Write the minimum code necessary to solve the problem correctly.
- Avoid overengineering and unnecessary abstractions.
- Do not create interfaces, services, factories, or repositories without a clear need.
- Reuse existing functionality before introducing new code.
- Prefer native .NET and Angular capabilities over additional dependencies.
- Preserve Clean Architecture boundaries.
- Do not introduce new design patterns unless justified.
- Avoid premature optimization.
- Keep methods focused and remove redundant code.
- Prefer modifying existing files over creating unnecessary new files.
- Do not refactor unrelated code.
- Preserve existing behavior and validate changes with tests.
- When multiple approaches work, choose the simplest maintainable solution.
## Scope and evidence

- These project rules apply to `backend/` and complement the root OMX contract; do not copy or replace its generated sections.
- Consult [README.md](README.md) and [docs/INDEX.md](docs/INDEX.md) only for the area relevant to the task.
- Verify project names and paths against [Liro.slnx](Liro.slnx) and the corresponding .csproj files.
- Preserve Domain → Application → Infrastructure dependency boundaries; executable hosts compose the existing layers. Do not introduce layers, projects or dependencies without a concrete task requirement.
- Distinguish implemented behavior from historical notes and planned modules; code and versioned configuration establish the current behavior.
- Keep HTTP documentation with backend contracts in `docs/frontend/`; Angular implementation documentation belongs in `../web/liro-web/`.
- Verify according to the change. Documentation-only work needs link/path and diff checks; do not start services or migrations to validate prose.
- Keep credentials and local runtime state out of documentation. Do not edit OMX-managed files or runtime markers as part of ordinary project cleanup.
- Use native subagents only for substantial independent work; execute trivial tasks directly.
