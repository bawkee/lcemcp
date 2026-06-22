# TODO

This file is the project working memory as a dev log appendix to the far more important `SPEC.md` which is the initial app's dev specification. Keep it in sensible implementation order, and add context here when a test result or decision is not already captured in `spec.md`.

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

Completed on 2026-06-19:

- Added migration 2, `message_bodies_and_search`, with `message_bodies`, `message_recipients`, `message_search_docs`, and the external-content SQLite FTS5 table `messages_fts`.
- Added triggers on `message_search_docs` so app-maintained search documents keep `messages_fts` current on insert/update/delete.
- Added lightweight body normalization: prefer plain text, fall back to readable HTML-to-text, collapse noisy whitespace, and persist To/CC/BCC/Reply-To recipients.
- Added `sync-bodies` CLI to fetch full message bodies for already-synced message locations and persist bodies/recipients/search docs without exposing message content in command output.
- Added local `search` CLI as the first `email_search` behavior over message metadata/body FTS. It supports bounded result limits, account filter, sender filter, folder-role filter, attachment metadata filter, sanitized terms/phrases/OR, and SQLite FTS hit-centered snippets.
- Added `SqlBinder` 1.0.0 for optional search filters, with FTS query text still parsed separately and bound as a normal SQLite parameter.
- `status` now reports message body and message search-doc counts.
- Added migration 3, `sync_runs_and_search_readiness`, with durable `sync_runs` plus persisted account `history_days` so readiness can compare metadata sync coverage with the configured searchable window.
- Added message-search readiness computation for account/sender/folder-role/attachment scopes. It reports metadata messages, indexed bodies, search-doc rows, FTS rows, pending bodies, complete metadata folders, and active sync-run progress.
- CLI `search` now returns `not_synced` and does not run FTS when the requested corpus is incomplete. `--allow-partial` is available as an explicit debug opt-in.
- CLI `sync` and `sync-bodies` now create durable sync-run records with phase, status, progress counts, elapsed/ETA fields, last error, and cancellation cleanup. Metadata sync updates progress by folder; body sync updates progress by pending message body target.
- Refactored SqlBinder usage from manual `Query.GetSql()` / `SqlParameters` copying to `DbQuery.CreateCommand()` with custom bracket placeholders. Raw SQLite parameters such as `$fts`, `$limit`, and `$snippetTokens` remain explicit app-owned parameters.
- `status` now prints message-search readiness and active sync-run progress.
- Added migration 4, `sync_queue_and_leases`, to replace naive progress-staleness cleanup with a lease-backed global sync queue. `sync_runs` now records scope/request/owner metadata, while `sync_leases` is the authority for who may sync.
- `sync` and `sync-bodies` now acquire a global sync lease before touching IMAP. A second process queues and waits its turn instead of starting duplicate provider work. Running syncs heartbeat the lease independently from progress counters.
- Expired sync leases are marked failed and released for crash recovery. Old `last_progress_at` alone no longer fails a run, and stale owners cannot mark a run succeeded after losing their lease.
- Follow-up sanity review tightened lease enforcement: expired leases cannot be heartbeated back to life, progress/completion failures now stop CLI success reporting, and IMAP sync paths check lease ownership before persisting fetched data or folder failure state.

Test result:

- `dotnet build lcemcp.slnx` succeeded on 2026-06-19.
- `dotnet test lcemcp.slnx` passed with 12 tests on 2026-06-19. Coverage now includes fresh schema v2 initialization, v1-to-v2 migration, already-migrated database preservation, body/search-doc upsert, FTS search, recipient search, stale FTS entry removal on body update, and the prior config/storage idempotence tests.
- Temp-config CLI `status` smoke test succeeded on 2026-06-19 and created schema version 2 with 0 messages, 0 bodies, and 0 search docs.
- Bounded live Yahoo body sync succeeded on 2026-06-19 with `sync-bodies --account yahoo --folder Inbox --max-per-folder 3 --batch-size 2`: selected 3, fetched 3, persisted 3, missing 0. Follow-up `status` reported schema version `2 / target 2`, 50 messages, 50 message locations, 3 message bodies, and 3 message search docs.
- CLI `search` smoke test with a random non-matching token and `--account yahoo --limit 5` succeeded on 2026-06-19 and returned 0 results without printing private snippets.
- `dotnet build lcemcp.slnx` succeeded on 2026-06-19 after readiness/sync-run changes.
- `dotnet test lcemcp.slnx` passed with 16 tests on 2026-06-19. New coverage includes schema v3 initialization/migration, search-readiness gating for missing bodies/search docs, capped metadata sync remaining not ready, active sync-run progress, and stale sync-run reconciliation.
- Temp-config CLI `status` smoke test succeeded on 2026-06-19 and created schema version 3 with message search readiness `not_synced` for an empty config.
- Temp-config CLI `search --query smoke --limit 5` smoke test succeeded on 2026-06-19 and returned `Search status: not_synced` / `Search results: not run`, without treating the empty incomplete corpus as a normal empty result.
- Temp-config CLI `search --query smoke --limit 5 --allow-partial` smoke test succeeded on 2026-06-19 after the `DbQuery` refactor and executed the FTS path with 0 results.
- `dotnet build lcemcp.slnx` succeeded on 2026-06-19 after sync lease/queue changes.
- `dotnet test lcemcp.slnx` passed with 19 tests on 2026-06-19. New coverage includes schema v4 initialization/migration, queued second sync runs, old progress with a live lease, expired lease recovery, and stale-owner completion refusal.
- Temp-config CLI `status` smoke test succeeded on 2026-06-19 and created schema version `4 / target 4` with no configured accounts.
- Sync lease sanity review on 2026-06-19 found and fixed two safety gaps: expired heartbeat revival and ignored owner-check failures in CLI progress/completion. `dotnet test lcemcp.slnx` passed with 23 tests, adding coverage for expired-running-lease crash recovery into a queued successor, expired heartbeat refusal, abandoned queued-run recovery, and wrong-owner heartbeat/progress/completion refusal. A temp-config CLI `status` smoke test still created schema version `4 / target 4`.
- Manual live Yahoo body sync on 2026-06-20 with `sync-bodies --account yahoo --folder Inbox --max-per-folder 0` completed, but showed no foreground progress for about 3 minutes between sync-run start and final folder summary. Root cause: body sync updated durable `sync_runs` progress after each target, but the foreground CLI only printed queued-wait progress and final results. Fixed the CLI to print active sync progress when the run starts and then about every 30 seconds from the sync lease heartbeat.
- `dotnet build lcemcp.slnx` and `dotnet test lcemcp.slnx` passed with 26 tests on 2026-06-20 after foreground sync-progress reporting was added.
- Rechecked the message-search readiness history-window predicate on 2026-06-20. The bug was real: a wider metadata sync window such as `since_days=60` was treated as not covering a 30-day configured history, while a narrower `since_days=10` could be treated as complete. Fixed readiness to consider `since_days=0` full sync or `since_days >= history_days` complete, and added regression coverage for wider/narrower windows. `dotnet build lcemcp.slnx` and `dotnet test lcemcp.slnx` passed with 28 tests.

Design decision on 2026-06-19:

- Search readiness must be binary for the requested scope. MCP `email_search` should return normal results only when the relevant corpus is fully indexed. If metadata, bodies, search docs, or FTS rows are incomplete for the requested account/folder/history/search scope, it should return `not_synced`/readiness status rather than an ordinary empty result set.
- Do not rely on partial search as default behavior. Partial search may exist later only as an explicit/debug opt-in such as `allow_partial: true`, and must label results as incomplete.
- Long-running sync should be durable and observable. `email_sync_now` should start/resume a sync run, return a `sync_run_id`, and expose phase/progress/elapsed/ETA/estimate-confidence through `email_get_sync_status`. MCP progress notifications can be used when available, but pollable status is the real contract because LLM harness support varies.
- LLM-facing tool descriptions should tell clients to check `email_get_sync_status`, trigger sync if needed, poll at reasonable intervals, and treat `not_synced` as "search not ready", never as evidence that no matching email exists.
- Sync liveness is based on `sync_leases` heartbeat/expiry, not `sync_runs.last_progress_at`. For v1, use one global sync scope for safety; per-account concurrent sync can be considered later after the MCP surface and provider behavior are better understood.

Next work:

- Run a few local searches against the live indexed Yahoo bodies without recording private snippets in TODO, just counts and whether snippets are usefully hit-centered.
- Add a local `get-message`/debug read command if needed before MCP exposure, so a synced message can be inspected through bounded, cited local storage rather than ad hoc database queries.
- Expand body sync after a small live soak: larger bounded runs, body re-fetch/reindex behavior, and error-state handling for messages whose body fetch repeatedly fails.
- Expose read-only local search through MCP after the stdio walking skeleton lands and readiness semantics are in place.

## 6. Add MCP Walking Skeleton

Once local status and basic storage exist, add MCP stdio without disrupting CLI diagnostics.

Next work:

Completed on 2026-06-20:

- Added the MCP stdio server command: `serve`.
- Implemented a small explicit line-delimited JSON-RPC stdio loop for `initialize`, `notifications/initialized`, `ping`, `tools/list`, and `tools/call`.
- Negotiates supported MCP protocol versions and defaults to the current official `2025-11-25` version when the client asks for an unsupported version.
- Ensured MCP diagnostics go to stderr; stdout is reserved for JSON-RPC messages only.
- Added first read-only MCP tool: `email_get_sync_status`.
- `email_get_sync_status` returns structured content plus text fallback, configured account status, config validation issues, binary message-search readiness, readiness counts, active sync-run progress, and optional known folder sync state.
- Added audit-log writes for `email_get_sync_status` tool calls.

Completed on 2026-06-20 follow-up:

- Expanded the MCP tool catalog beyond status-only. MCP now exposes `email_list_accounts`, `email_list_folders`, `email_search`, `email_get_message`, `email_sync_now`, and `email_get_sync_status`.
- `email_search` uses the same local FTS path and binary readiness gate as the CLI. It returns `not_synced` with readiness/progress details rather than ordinary empty results when the requested corpus is incomplete. `allow_partial` remains an explicit debug opt-in.
- MCP `email_search` resolves account scope through enabled configured accounts before querying SQLite, so stale local database rows for removed/unconfigured accounts are not exposed through MCP.
- `email_get_message` reads one locally cached message by stable local `message_id`, returns bounded body text, sender/recipient/folder context, and does not expose raw filesystem paths or raw provider access.
- `email_sync_now` starts or queues metadata+body indexing for configured accounts, returns quickly with a durable `sync_run_id`, and runs provider work on a background task using the existing global sync lease/queue. Clients should poll `email_get_sync_status`.
- Added local storage helpers for bounded message reads, sync-run phase changes, affected-message audit ids, account sync summaries, and folder `last_sync_at`.
- MCP tool calls are audited to `audit_log`; search/message calls include affected local message ids when available.

Test result:

- `dotnet build lcemcp.slnx` succeeded on 2026-06-20.
- `dotnet test lcemcp.slnx` passed with 25 tests on 2026-06-20. New coverage exercises MCP initialize/tools-list behavior, stdout/stderr separation, `email_get_sync_status` structured readiness output, and audit logging.
- Temp-config CLI stdio smoke succeeded on 2026-06-20: `serve` returned valid JSON-RPC for initialize and tools/list on stdout, while the startup diagnostic went to stderr.
- `dotnet build lcemcp.slnx` succeeded after the expanded MCP catalog on 2026-06-20.
- `dotnet test lcemcp.slnx` passed with 26 tests on 2026-06-20. New coverage drives a ready local message index through MCP `email_search` and `email_get_message`, including audit rows with affected message ids.
- Temp-config CLI stdio smoke succeeded on 2026-06-20 after the expanded catalog: `tools/list` returned all six MCP tools, and `email_search` returned structured `not_synced` output on stdout with diagnostics only on stderr.
- Live real-config MCP smoke succeeded on 2026-06-20 without recording private snippets/subjects/senders:
  - Initial `email_get_sync_status` for Yahoo reported `search_ready=false`, 50 metadata messages, and 3 indexed messages.
  - Normal `email_search` for Yahoo Inbox returned `status=not_synced` and 0 results, as intended for the incomplete corpus.
  - Bounded `email_sync_now` for Yahoo Inbox with `since_days=3` and `max_per_folder=2` returned a running `sync_run_id`; same-server polling showed phase changes from `syncing_metadata` to `syncing_bodies`.
  - The bounded sync completed within the polling window and advanced the real cache to 52 metadata messages and 5 indexed bodies/search docs.
  - `dotnet-script` 2.0.1 was installed for future smoke harnesses. A follow-up MCP `email_search` with `allow_partial=true` over generic terms returned `status=partial`, 2 results, 2 snippets, and both snippets included FTS hit markers. Snippet text was not recorded.
- Follow-up real database inspection on 2026-06-20 explained the 52-message / 5-FTS-row gap: all 5 downloaded bodies had matching `message_search_docs` and `messages_fts` rows, while the other 47 messages were still `body_downloaded = 0`. This is expected after the deliberately bounded body syncs, not an FTS trigger failure. The only recorded metadata sync state was Yahoo Inbox with `since_days=3` and `max_per_folder=2`, so default all-folder readiness also correctly remained `not_synced`.

Next work:

- Expand body sync toward full readiness for the configured history window, then rerun normal MCP `email_search` without `allow_partial`.
- Add `email_get_audit_events` once there is enough audit history to inspect through MCP.
- Later MCP tools remain `email_get_thread` and attachment-text search/read once those local features exist.

## 7. MCP MVP Gaps From User-Context Review

These came from reviewing what happens when a normal Codex user asks something like "find me OpenAI invoices from the past 2 months" after installing/configuring the MCP. Keep this list focused on MVP read-only search quality and avoid re-listing known attachment work as a surprise bug.

Legit near-term issues:

- Add `date_from` / `date_to` to `email_search` and CLI search. This should bind against the message date (`COALESCE(date_sent, date_received)`) instead of making the LLM filter dates from returned snippets. Search readiness/results should also include an explicit coverage note when the requested date range extends beyond the configured/synced account history, rather than silently implying the whole request was searched.
- Add recipient filtering, starting with `to_email` and likely broader recipient/participant filters over `message_recipients`. Current MCP only supports `from_email`.
- Implement real pagination/cursors for `email_search`. Current responses can set `has_more=true`, but `next_cursor` is always `null`, so callers cannot continue without changing the query or limit.
- Make the sync-history default user-visible and deliberately chosen. Current implementation defaults account `history_days` to 30 development days, while `SPEC.md` recommends 1095 days, which is probably too high for normal first-run use. Decide the product default, then show it clearly in setup/help/status/MCP account status and document exactly where/how users change it (`config.toml`, setup options, later admin UI).

Known deferred or acceptable for MVP:

- Attachment metadata, attachment text extraction, `search_in`, attachment search, attachment result hits, and `email_get_attachment_text` are already tracked under later milestones. They remain important, especially for invoice PDFs, but are intentionally after the first read-only message-search MVP.
- `email_get_thread` is not Gmail-only in the spec. Gmail provider IDs can improve confidence, but the current metadata model already stores provider thread keys when available and falls back toward header/thread heuristics. `SPEC.md` Milestone 1 includes `email_get_thread`; TODO currently tracks it as a later MCP tool after the local message read/search surface is stable.
- Periodic/background sync is still future service/webserver/admin work. For now, MCP-triggered `email_sync_now` plus pollable `email_get_sync_status` is acceptable.

## 8. Add Focused Tests

The project is still young, so test only the parts where regressions would be annoying or dangerous.

Next work:

- Completed on 2026-06-19: Added `tests/LceMcp.Tests` with unit tests for config load/save round trips, config validation errors, and credential target generation without touching the OS credential store.
- Completed on 2026-06-19: Added SQLite integration tests for schema v2 initialization, prototype rebuild, account/folder upsert idempotence, message metadata upsert idempotence, Message-ID fallback matching, and rollback on failed message-location insert.
- Completed on 2026-06-19: Updated SQLite integration tests for migration mode. `dotnet test lcemcp.slnx` passed with 10 tests covering fresh migration initialization, already-migrated database preservation, and the previous storage idempotence/rollback behavior.
- Completed on 2026-06-19: Added migration/search tests for schema version 2 body/search migration, v1-to-v2 migration preservation, body/recipient/search-doc persistence, FTS search, recipient search, and FTS cleanup on body updates. `dotnet test lcemcp.slnx` passed with 12 tests.
- Completed on 2026-06-19: Added readiness/sync-run tests for schema version 3, binary search readiness, capped metadata detection, active sync-run progress, and stale sync-run reconciliation. `dotnet test lcemcp.slnx` passed with 16 tests.
- Completed on 2026-06-19: Added sync lease/queue tests for schema version 4, queued second run behavior, old progress with live lease, expired lease recovery, and stale owner completion refusal. `dotnet test lcemcp.slnx` passed with 19 tests.
- Completed on 2026-06-19: Expanded sync lease tests after review to cover expired heartbeat refusal, queued successor claim after a crashed running owner, abandoned queued-run cleanup, and wrong-owner heartbeat/progress/completion refusal. `dotnet test lcemcp.slnx` passed with 23 tests.
- Completed on 2026-06-20: Added MCP stdio tests for initialize/tools-list, stdout/stderr separation, `email_get_sync_status` structured readiness output, and audit logging. `dotnet test lcemcp.slnx` passed with 25 tests.
- Completed on 2026-06-20: Expanded MCP tests to cover the full exposed tool catalog plus ready-index `email_search` and `email_get_message`, including affected-message audit ids. `dotnet test lcemcp.slnx` passed with 26 tests.
- Add an optional manual IMAP smoke test path that requires a configured real account and is not run by default.

## 9. Later Milestones

These are intentionally after read-only local search works.

Next work:

- Attachment metadata and text extraction.
- PDF embedded text extraction.
- Local admin UI.
- Draft/send support, disabled by default.
- SQLCipher or other database encryption support.
- Better provider presets, including Gmail and Microsoft OAuth.
