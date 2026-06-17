# AGENTS.md

## Dev Guidelines

- When continuing work, read `TODO.md` after this file. It contains current project working memory, phase order, and test results that may not exist in `SPEC.md`.
- Keep `TODO.md` updated when a new decision, manual test result, or implementation checkpoint changes the next sensible step.
- Build the project as a local-first email cache, search engine, and MCP permission boundary. The AI client is the reasoning layer; this app should provide durable, bounded, cited email access.
- Keep side effects at the edges: IMAP, SMTP, SQLite, filesystem, MCP transport, and logging.
- Keep it pragmatic SOLID. Prefer simple, testable boundaries over ceremony.
- Prefer functional style for parsing, normalization, ranking, and snippet extraction.
- Prefer composition over inheritance.
- Avoid Clean Architecture ceremony and interface-per-class bloat.
- Prefer readable code over clever code.
- Add abstractions only when they make sync, search, storage, or MCP behavior easier to understand.
- Use modern C# features where they improve clarity.
- Use `var` when the right-hand side makes the type obvious.
- Use target-typed `new()` when the target type is clear.
- Use file-scoped namespaces.
- Keep implicit usings enabled and nullable reference types disabled unless the project direction changes deliberately.
- Prefer records or record structs for simple immutable data carriers when useful.
- Prefer pattern matching and collection expressions when they simplify the code.
- Avoid EF Core in v1. The schema is small, query behavior matters, and SQLite FTS5 is easier to reason about with explicit SQL, use **SqlBinder** instead.
- For SQL-related work, especially SQLite FTS5 or optional search filters, read `skills/sqlbinder-skill.md` before editing query/storage code.
- Do not expose raw SQL, raw filesystem access, destructive mail actions, or raw attachment export through MCP tools by default.
- Log MCP calls when MCP support lands, and keep logs compatible with stdio transport by writing diagnostics to stderr.
