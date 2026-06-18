# TODO

This file is the project working memory. Keep it in sensible implementation order, and add context here when a test result or decision is not already captured in `spec.md`.

## 1. Close The Yahoo IMAP Probe Slice

The first live Yahoo account probe succeeded on 2026-06-17 using `imap.mail.yahoo.com:993` with SSL and an app password stored in Windows Credential Manager.

Observed real-account result:

- Authentication succeeded for `bojan.sala@yahoo.com`.
- Yahoo advertised capabilities including `IMAP4rev1`, `Idle`, `Namespace`, `SpecialUse`, `Move`, `ObjectID`, and `ListStatus`.
- Folder discovery returned 16 folders.
- Yahoo folder names included `Inbox` rather than uppercase `INBOX`, plus `Archive`, `Bulk`, `Draft`, `Sent`, and `Trash` with useful special-use attributes.
- Opening Inbox worked and reported `Messages: 10000`, `Recent: 0`, `UID validity: 1232404279`.
- Searching the last 30 days matched 174 UIDs.
- The probe said it was fetching 5 summaries, but printed 4 summaries. Investigate whether one requested UID was expunged, not returned by Yahoo, or filtered by summary ordering/printing.

Next work:

Completed on 2026-06-18:

- Added account config validation before IMAP network calls; `accounts` and `status` now surface validation issues without hiding other state.
- The IMAP probe now prints `Fetched summaries: n of m requested` and lists missing requested UIDs, which should explain the prior 5-requested/4-printed Yahoo result on the next live run.
- Folder resolution now prefers discovered folder full names/names before falling back to `client.Inbox`, so Yahoo's `Inbox` casing is respected.
- Folder probe output now includes inferred role and selectable state, and the initial `folders` schema has columns for attributes, role, selectable state, UIDVALIDITY, message count, and recent count.

Next work:

- Run another live Yahoo probe and record whether the missing summary was expunged/not returned or a probe-ordering issue.
- Persist folder special-use attributes during discovery rather than only printing them.

## 2. Add Walking Skeleton Storage

The spec wants SQLite as the durable local cache, but the first probe intentionally skipped database work. The next durable step is schema initialization without syncing bodies yet.

Next work:

Completed on 2026-06-18:

- Added `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3`; no EF Core.
- Extended app paths with `email.db`, `attachments`, and `logs` under the existing `%APPDATA%\lcemcp`-style config directory.
- Added SQLite schema initialization for `schema_migrations`, `accounts`, `folders`, `sync_state`, and minimal `audit_log`.
- Chose `schema_migrations` from `SPEC.md` as the durable schema-version ledger; `status` reports the current schema version from that table.
- Added `status`, which initializes storage if needed and reports config presence, database path, config validation, database account/folder counts, and last sync state.
- Added lightweight migration infrastructure, but migrations are intentionally locked during the prototype phase. While `DatabaseMigrations.MigrationsLocked = true`, `DatabaseMigrations.All` must stay empty and schema changes rebuild `email.db` when the prototype schema marker changes. When the MVP schema is stable, remove the lock deliberately, add a complete migration 1, delete/recreate the prototype DB one final time, and then preserve DBs with forward-only migrations.

Test result:

- `dotnet build lcemcp.slnx` succeeded on 2026-06-18.
- `LCEMCP_CONFIG_DIR=<temp> dotnet run --project src\LceMcp -- status` succeeded, created `email.db`, reported schema version 1, 0 database accounts, 0 folders, and no sync state.
- Prototype migration-lock smoke test succeeded on 2026-06-18: fresh temp DB reported `created`, a second status reported `present`, and a deliberately invalid `email.db` was deleted/recreated with status `recreated for prototype schema`.

Next work:

- Add an integration test for SQLite schema initialization once the first test project exists.
- Continue with persisted account and folder discovery.

## 3. Persist Account And Folder Discovery

Once SQLite exists, turn the successful IMAP folder probe into persisted account/folder metadata.

Next work:

- Upsert configured accounts into the database while keeping secrets only in Windows Credential Manager.
- Discover folders per account and persist full name, display name, delimiter, attributes, special-use role, selectable/no-select state, UIDVALIDITY, message count, and recent count.
- Be conservative with Yahoo quirks: do not assume every provider uses the same folder names.
- Add a CLI command such as `folders --account yahoo` that reads from local storage after discovery.

## 4. Implement Metadata Sync

After account/folder discovery is durable, sync message envelopes for a bounded window before fetching full bodies.

Next work:

- Add `messages` and `message_locations` tables from the spec, scoped to a pragmatic first subset.
- Sync newest messages first for a configurable `history_days`.
- Commit in small batches and advance `sync_state` only after a batch succeeds.
- Store stable provider IDs where available. Yahoo advertises `ObjectID`, so investigate whether MailKit exposes a useful provider-stable ID before relying on Message-ID fallback.
- Make all upserts idempotent.

## 5. Implement Body Sync And Local Search

After metadata sync works, fetch bodies and build the first local search path.

Next work:

- Add `message_bodies`, `message_recipients`, `message_search_docs`, and SQLite FTS5 index tables.
- Normalize plain text and HTML-to-text enough for reliable search snippets.
- Implement hit-centered snippets instead of returning message beginnings.
- Add local `email_search` behavior behind CLI first, then expose via MCP.

## 6. Add MCP Walking Skeleton

Once local status and basic storage exist, add MCP stdio without disrupting CLI diagnostics.

Next work:

- Add the MCP stdio server command: `serve`.
- Ensure diagnostics/logging go to stderr so JSON-RPC stdout stays clean.
- Start with `email_get_sync_status`.
- Add read-only tools in this order: `email_list_accounts`, `email_list_folders`, `email_search`, `email_get_message`, `email_sync_now`.
- Log every MCP call to `audit_log`.

## 7. Add Focused Tests

The project is still young, so test only the parts where regressions would be annoying or dangerous.

Next work:

- Unit-test config load/save round trips.
- Unit-test config validation errors.
- Unit-test credential target generation without touching the OS credential store.
- Integration-test SQLite schema initialization.
- Add an optional manual IMAP smoke test path that requires a configured real account and is not run by default.

## 8. Later Milestones

These are intentionally after read-only local search works.

Next work:

- Attachment metadata and text extraction.
- PDF embedded text extraction.
- Local admin UI.
- Draft/send support, disabled by default.
- SQLCipher or other database encryption support.
- Better provider presets, including Gmail and Microsoft OAuth.
