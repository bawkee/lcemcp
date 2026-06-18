# lcemcp

`lcemcp` is an in-progress local-first email cache and MCP server. The goal is to give AI clients fast, bounded, cited access to your mail without turning IMAP itself into the reasoning layer.

Most IMAP MCPs act like thin remote mail wrappers: search a provider, fetch a few messages, return whatever the mailbox gives back. `lcemcp` is meant to be different:

- Sync mail into a local SQLite cache so search can work across accounts, folders, bodies, and eventually attachment text.
- Use SQLite FTS5 and metadata filters before involving the AI, returning compact hit-centered snippets instead of whole inbox dumps.
- Keep stable local IDs for messages, threads, folders, and attachments so agents can cite and retrieve evidence selectively.
- Treat MCP as a permission boundary: no raw SQL, no raw filesystem access, no destructive mail actions by default, and audited tool calls once MCP lands.
- Stay provider-agnostic where possible through IMAP/SMTP, while handling provider quirks like Yahoo folder names and app passwords.

The project is early. Right now the working slice is a Yahoo IMAP probe with local TOML account config and Windows Credential Manager password storage. SQLite storage, local search, and MCP tools are still being built.

```powershell
dotnet run --project src/LceMcp -- setup-yahoo --email you@yahoo.com --name Yahoo
dotnet run --project src/LceMcp -- status
dotnet run --project src/LceMcp -- accounts
dotnet run --project src/LceMcp -- credential-test --account yahoo
dotnet run --project src/LceMcp -- imap-test --account yahoo --limit 5
dotnet run --project src/LceMcp -- imap-test --account yahoo --query "refund processed" --limit 5 --fetch-first-body
```

Config currently defaults to `%APPDATA%\lcemcp\config.toml`.
