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

Follow-up result on 2026-06-18:

- A bounded live Yahoo probe with `--since-days 3 --limit 3` matched 16 UIDs and fetched `3 of 3 requested`; the prior 5-requested/4-returned result did not reproduce in this small window.

Follow-up result on 2026-06-19:

- A wider live Yahoo Inbox metadata sync with `--since-days 30 --max-per-folder 200 --batch-size 25` matched 170 UIDs, selected 170, fetched 170, persisted 170, reported `missing=0`, and reached highest UID 283547. The older 5-requested/4-returned probe result did not reproduce in this wider real sync and is not a current blocker unless it recurs.

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

Completed on 2026-06-19:

- Removed the prototype migration lock and prototype initializer. Migration mode is now the only schema initialization path.
- Added migration 1, `initial_metadata_cache`, as the complete current metadata-cache schema.
- Decided not to keep a legacy prototype-database bridge because no one else is using the app yet. From this point forward databases are preserved with explicit forward-only migrations.
- The real configured database at `C:\Users\bojan\AppData\Roaming\lcemcp\email.db` was recreated once under migration tracking. Follow-up `status` reported schema version `1 / target 1`.

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

Completed on 2026-06-18:

- Added reusable IMAP folder discovery metadata and shared role inference with the existing probe path.
- Upsert configured account metadata into SQLite from `setup-yahoo` and `discover-folders`, while keeping secrets only in Windows Credential Manager.
- Added `discover-folders --account <id-or-email>` to connect to IMAP, discover folders, and persist full name, display name, delimiter, attributes, inferred role, selectable state, UIDVALIDITY, message count, recent count, and discovery time.
- Added `folders --account <id-or-email>` to read persisted folders from local SQLite storage without touching IMAP.
- Live Yahoo discovery succeeded for `yahoo` on 2026-06-18: 16 folders discovered and persisted; `status` then reported 1 database account and 16 database folders.

Next work:

- Continue with metadata sync.
- Add focused tests once the first test project exists, especially around account/folder upsert idempotence and folder listing.

## 4. Implement Metadata Sync

After account/folder discovery is durable, sync message envelopes for a bounded window before fetching full bodies.

Next work:

Completed on 2026-06-19:

- Added prototype schema v2 with `messages` and `message_locations`, plus status counts for stored messages and message locations. Because migrations are still locked, old prototype DBs rebuild under the new prototype marker.
- Added `sync` CLI for bounded envelope metadata sync. It syncs newest messages first, uses account `history_days` by default, supports `--since-days`, `--folder`, `--max-per-folder`, and `--batch-size`, and auto-discovers folders when the local folder cache is empty after a prototype reset.
- Metadata fetch requests include MailKit `EmailId`/`ThreadId` when the server advertises `ObjectID`, and Gmail IDs/labels when `GMailExt1` is available. Yahoo ObjectID values were confirmed in the database as `emailid:*` provider keys and `threadid:*` thread keys.
- Message upserts are idempotent by existing folder UID location first, then provider message key, then `Message-ID` header fallback. `message_locations` is idempotent on `(account_id, folder_id, provider_uid)`.
- Sync commits each fetched batch and advances `sync_state`, `folders.last_uid`, `folders.last_sync_at`, and `accounts.last_sync_at` only after the batch transaction succeeds.

Test result:

- `dotnet build lcemcp.slnx` succeeded on 2026-06-19.
- Temp-config `status` smoke test created schema version 2 and reported 0 messages / 0 message locations.
- Live Yahoo metadata sync succeeded on 2026-06-19 with `sync --account yahoo --folder Inbox --since-days 3 --max-per-folder 5 --batch-size 2`: auto-discovered 16 folders after prototype reset, matched 16 Inbox UIDs, selected 5, fetched 5, persisted 5, missing 0, highest UID 283547.
- Re-running the same live sync stayed idempotent: status still reported 5 messages and 5 message locations.
- Added the first xUnit test project on 2026-06-19. `dotnet test lcemcp.slnx` passed with 10 tests covering config save/load, config validation, credential target generation, schema v2 initialization, prototype rebuild, account/folder upsert idempotence, metadata upsert idempotence by location/provider key, Message-ID fallback matching, and transaction rollback on a failed location insert.
- Temp-config CLI `status` smoke test also succeeded on 2026-06-19 and created schema version 2 in an isolated config directory.
- Wider live Yahoo Inbox metadata sync succeeded on 2026-06-19 with `sync --account yahoo --folder Inbox --since-days 30 --max-per-folder 200 --batch-size 25`: matched 170, selected 170, fetched 170, persisted 170, missing 0, highest UID 283547. Follow-up `status` reported 1 account, 16 folders, 170 messages, 170 message locations, and last sync state `yahoo/Inbox`.
- After migration unlock on 2026-06-19, live Yahoo Inbox sync succeeded with `sync --account yahoo --folder Inbox --since-days 30 --max-per-folder 50 --batch-size 25`: auto-discovered 16 folders into the newly recreated migration-tracked DB, matched 170 Inbox UIDs, selected 50, fetched 50, persisted 50, missing 0, highest UID 283547. Follow-up `status` reported schema version `1 / target 1`, 1 account, 16 folders, 50 messages, 50 message locations, and last sync state `yahoo/Inbox`.

Next work:

- Add a local message-list/debug command if needed before body sync, so synced metadata can be inspected without ad hoc database queries.
- Continue with body sync and local search once metadata sync has had a slightly wider live run.

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

- Completed on 2026-06-19: Added `tests/LceMcp.Tests` with unit tests for config load/save round trips, config validation errors, and credential target generation without touching the OS credential store.
- Completed on 2026-06-19: Added SQLite integration tests for schema v2 initialization, prototype rebuild, account/folder upsert idempotence, message metadata upsert idempotence, Message-ID fallback matching, and rollback on failed message-location insert.
- Completed on 2026-06-19: Updated SQLite integration tests for migration mode. `dotnet test lcemcp.slnx` passed with 10 tests covering fresh migration initialization, already-migrated database preservation, and the previous storage idempotence/rollback behavior.
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
