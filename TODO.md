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

- Make `email_search` query text optional when the caller supplies bounded metadata filters. The tool should support date-only and filter-only browsing, such as "show Yahoo messages from June", without forcing an LLM to invent broad synthetic search terms. Implementation needs a non-FTS filtered listing path with deterministic date/message-id ordering, normal readiness checks, cursor pagination, and CLI parity.
- Add `date_from` / `date_to` to `email_search` and CLI search. This should bind against the message date (`COALESCE(date_sent, date_received)`) instead of making the LLM filter dates from returned snippets. Search readiness/results should also include an explicit coverage note when the requested date range extends beyond the configured/synced account history, rather than silently implying the whole request was searched.
- Add recipient filtering, starting with `to_email` and likely broader recipient/participant filters over `message_recipients`. Current MCP only supports `from_email`.
- Implement real pagination/cursors for `email_search`. Current responses can set `has_more=true`, but `next_cursor` is always `null`, so callers cannot continue without changing the query or limit.
- Fix explicit one-off folder sync. Today `ReadSyncFolders(account, folder)` filters to `sync_enabled = true` before applying an explicit folder filter, so a direct MCP `email_sync_now` for a disabled-but-selectable folder such as Yahoo `Important` selects zero folders. The spec now says an explicit folder path/name/id should override the default `sync_enabled` scope for that one run; if no selectable folder matches, reject immediately instead of accepting a zero-folder run.
- Improve estimate/list UX for non-selected folders. `email_estimate_sync` intentionally defaults to selected sync-enabled folders, but the response should make omitted selectable folders obvious enough that an LLM does not mistake the default estimate for "all folders". Consider returning omitted/selectable counts and a hint to call `email_list_folders` or pass explicit `folders`.
- Make sync setup explicit and harness-friendly:
  - Update implementation defaults to match `SPEC.md`: first-run `history_days = 90`, with larger windows such as 365/1095/everything treated as deliberate per-run or config choices.
  - Treat `history_days` in `config.toml` as the default requested window, not a hard cap. MCP tools must not silently rewrite `config.toml`; one-off wider backfills should pass `since_days` to `email_sync_now`.
  - Add automatic gap expansion in metadata sync. For each account/folder, compute `effective_since_days = max(requested_since_days, days since latest successful uncapped metadata sync + small overlap)`, with `0` still meaning no date bound. Do not count capped runs as closing gaps.
  - Expose `requested_since_days`, `effective_since_days`, and `auto_expanded_for_gap` in CLI/MCP sync output and `email_get_sync_status` so LLM clients can explain sync scope without inferring it.
  - Change folder discovery defaults from "all selectable folders sync" to role-based sync choices: default on for inbox/sent/archive/all_mail; default off for spam/junk/bulk/trash/deleted/drafts/outbox/custom/unknown. Rediscovery must preserve existing choices.
  - Add MCP setup/onboarding tools: `email_get_setup_status` with only setup statuses (`config_invalid`, `credential_missing`, `folders_not_discovered`, `setup_complete`), `email_discover_folders` for provider discovery without message sync, and `email_estimate_sync` for factual counts/estimates without warning prose.
  - Keep or add CLI parity for practical users: existing `discover-folders`/`folders` already cover discovery/listing, but add a durable way to inspect/set folder sync choices and ensure help/status document `history_days`, per-run `--since-days`, and automatic effective-window expansion.

Design decision on 2026-06-22:

- Avoid a full sync-coverage ledger for MVP. The simpler contract is: `history_days` is the default requested window, explicit `since_days` is a one-run override, and sync automatically widens to cover any gap since the latest successful uncapped sync for the same account/folder. This prevents holes after long idle periods without mutating user config.
- `email_get_setup_status` is setup-only, not a sync/readiness state machine. Sync progress/readiness remains under `email_get_sync_status`.
- `email_estimate_sync` should return facts such as selected folders, requested/effective window, estimated messages, estimate source, and confidence. It should not decide whether the result is "too large"; the LLM/user can make that call.

Completed on 2026-06-22:

- Added `date_from` / `date_to` to CLI and MCP `email_search`, normalized date-only bounds inclusively, and included readiness coverage notes when a requested range needs wider or full-history metadata coverage.
- Added `to_email` filtering over durable `message_recipients` for search results. Readiness remains conservative because recipient rows are only complete after body sync.
- Added real opaque cursor pagination for MCP and CLI search. Search now uses a deterministic `(score, date, message_id)` order and returns a usable `next_cursor` when more results are available.
- Updated first-run defaults to `history_days = 90` in config/account defaults and migration defaults. Existing configured accounts are not silently rewritten.
- Added role-based folder sync defaults for newly discovered folders: inbox/sent/archive/all_mail default on, trash/spam/drafts/custom/unknown default off. Rediscovery preserves existing `sync_enabled` choices.
- Added automatic metadata sync gap expansion from the latest successful uncapped folder sync, with a small overlap. Sync state records requested/effective windows, and sync runs/status expose `requested_since_days`, `effective_since_days`, and `auto_expanded_for_gap`.
- Added MCP setup/onboarding tools: `email_get_setup_status`, `email_discover_folders`, and a cached-count MVP of `email_estimate_sync`. The estimate tool accepts `probe` but currently reports `probe_honored=false` rather than performing a provider probe.
- Added CLI `set-folder-sync` plus help text for folder sync choices, search date/recipient/cursor filters, and automatic effective-window expansion.

Test result:

- `dotnet build lcemcp.slnx` succeeded on 2026-06-22.
- `dotnet test lcemcp.slnx` passed with 33 tests on 2026-06-22. New coverage includes schema v5 migration, folder role defaults/preservation, search date/recipient/cursor behavior, date coverage readiness notes, sync-window auto expansion ignoring capped runs, expanded MCP tool listing, and setup-status output.

Manual test result on 2026-06-22:

- Release verification succeeded: `dotnet test lcemcp.slnx /p:UseSharedCompilation=false` passed with 33 tests, and `dotnet build lcemcp.slnx -c Release /p:UseSharedCompilation=false` succeeded.
- Temp-config CLI smoke through the Release executable succeeded: `status` created schema version 5 in an isolated config directory, and a no-hit `search` correctly returned `not_synced` without running FTS.
- Before wiring to a real LLM, the old prototype-era Yahoo folder sync choices were tightened to the role-default MVP posture: Inbox, Sent, and Archive are sync-enabled; custom, spam, drafts, trash, and other non-default folders are disabled.
- Published the MCP executable to ignored local artifact path `artifacts\lcemcp-mcp`, and added a Codex MCP server entry named `lcemcp` in `C:\Users\bojan\.codex\config.toml`, pointing at `artifacts\lcemcp-mcp\LceMcp.exe serve`. The Codex config parsed as valid TOML.
- Direct JSON-RPC stdio smoke against the configured Release executable succeeded for `initialize`, `tools/list`, `email_get_setup_status`, and `email_get_sync_status`. The server advertised 10 tools and reported 3 sync-enabled folders.
- Live Yahoo discovery/sync attempted through the Release executable, but Yahoo rejected LOGIN. The Windows Credential Manager target exists, so the current blocker appears to be an invalid/expired app password rather than missing local setup. Default search readiness remains `not_synced` for the 30-day, 3-folder scope until the credential is refreshed and a full metadata/body sync can complete.
- No-sync Yahoo auth retest later on 2026-06-22 still failed at LOGIN. `imap-test --account yahoo --limit 1` connected to `imap.mail.yahoo.com:993`, read capabilities, then received `LOGIN Invalid credentials`. Credential Manager metadata looked sane: target `lcemcp/imap/yahoo`, username `bojan.sala@yahoo.com`, 16-character secret, no whitespace, no control characters. A direct MCP `email_discover_folders` call also failed with the same provider error without starting message sync.
- Added `credential-update` on 2026-06-22 so an existing account password/app password can be replaced without rerunning setup or rewriting account config. Usage: `credential-update --id yahoo` or `credential-update --account yahoo`; `--password-stdin` is available for non-interactive use. `credential-test` and `credential-delete` also accept `--id` as an alias for `--account`.
- Release publish was refreshed after `credential-update`. `dotnet test lcemcp.slnx /p:UseSharedCompilation=false` passed with 33 tests, Release build succeeded, and an isolated smoke against the published executable created a throwaway account credential, updated it via `credential-update --id <temp> --password-stdin`, verified it existed, and deleted it.

User-context MCP test result on 2026-06-22:

- A Codex MCP run against the real Yahoo account reached normal search readiness for the current default scope: 176 indexed messages across the three sync-enabled folders, Archive, Inbox, and Sent.
- The run exposed a real search flexibility gap: `email_search` rejects a blank query, so the agent could not ask for "all June mail" with date/account filters only. It had to use broad synthetic searches and targeted follow-ups instead.
- The `Important` folder oddity was not a Yahoo provider issue. Local folder metadata shows `Important` is selectable but `sync_enabled=false` and `last_sync=-`. The implementation path uses `ReadSyncFolders`, which excludes disabled folders before applying the explicit folder filter. Result: a targeted `email_sync_now` for `Important` can be accepted as a run but select no folders, while `email_get_sync_status` continues to report readiness for the default three sync-enabled folders.
- The likely third issue from the transcript is estimate-scope discoverability: `email_estimate_sync` without explicit `folders` reports only selected sync-enabled folders by design. The agent noticed and explicitly estimated non-selected folders afterward, but the default response should make that scope more obvious.

Packaging result on 2026-06-24:

- Added `scripts/build-release.ps1` to produce a self-contained single-file Windows release artifact. Default output is the ignored folder `artifacts\lcemcp`, not the older redundant `artifacts\lcemcp-mcp`.
- Ran the build script successfully. It ran `dotnet test lcemcp.slnx -c Release /p:UseSharedCompilation=false` with 33 passing tests, then published `artifacts\lcemcp\LceMcp.exe` for `win-x64`.
- Verified the published artifact is a single file and smoke-tested `LceMcp.exe serve` with MCP `initialize` and `tools/list`.
- Removed the stale generated `artifacts\lcemcp-mcp` folder after stopping a leftover old `LceMcp.exe` process that still held files open.
- Added `mcp-config --client codex` so the executable can print the exact Codex TOML block for the running package path. `help` now advertises this install helper so an agent can discover it by running the exe. Rebuilt the single-file artifact and verified `mcp-config`, `help`, and MCP `initialize`/`tools-list` against `artifacts\lcemcp\LceMcp.exe`.
- Added a short README copy/paste prompt for safer agent-mediated audit/install. The prompt tells agents to clone to temp, make a sanitized source/build-only review copy, strip comments, inspect for suspicious behavior and MCP/secret boundaries, build with `scripts/build-release.ps1`, print `mcp-config --client codex`, and only edit Codex config after user approval.

Freshness result on 2026-06-24:

- Added `email_search` freshness output so harnesses can distinguish "local index is ready" from "local cache is current enough for this question." Search responses now include `freshness.source=local_cache`, `response_generated_at`, conservative `search_scope_as_of`, intuitive `last_sync_performed_at`, oldest/newest scoped sync timestamps, `cache_age_seconds`, requested date bounds, requested upper bound, and `requested_range_extends_beyond_cache`.
- Kept `search_ready` semantics unchanged: it only means the local index is complete for the requested scope. Freshness is a separate axis the LLM can use to decide whether to call `email_sync_now`, especially for "today" or "this week" prompts.
- CLI `search` now prints a one-line freshness summary after the status line.
- `dotnet test lcemcp.slnx /p:UseSharedCompilation=false` passed with 34 tests, adding coverage for ready local search over two folders where `search_scope_as_of` is the older scoped folder sync and `last_sync_performed_at` is the newer one.
- `dotnet build lcemcp.slnx /p:UseSharedCompilation=false` succeeded, and a live local CLI/MCP smoke showed ready Yahoo search responses carrying freshness with `requested_range_extends_beyond_cache=true` for an open-ended stale cache.
- Refreshed the single-file release artifact at `artifacts\lcemcp\LceMcp.exe` after stopping stale `serve` processes that held the old executable open. Release tests passed with 34 tests and the new artifact SHA256 is `FB27613061F4A8751003671ACC308981991495101E7B34040555BDDD9A65DE1C`.

Harness config result on 2026-06-24:

- Extended `mcp-config --client` beyond Codex. Supported targets are now `codex`, `claude-code`, `opencode`, `github-copilot`, and `vscode`, with aliases such as `claude`, `copilot`, `copilot-cli`, and `copilot-vscode`.
- The helper now emits Codex TOML, Claude Code `.mcp.json`-style JSON, OpenCode `opencode.json` local MCP JSON, GitHub Copilot CLI `~/.copilot/mcp-config.json` JSON, and VS Code/GitHub Copilot `.vscode/mcp.json` JSON. The existing `dotnet <dll> serve` fallback is preserved for framework-dependent builds.
- Updated the README safe audit/install prompt to use the chosen client rather than assuming Codex.
- Added focused CLI tests for the new config outputs, aliases, and `.dll` command handling. `dotnet build lcemcp.slnx /p:UseSharedCompilation=false` succeeded, and `dotnet test lcemcp.slnx /p:UseSharedCompilation=false` passed with 41 tests.
- Refreshed the single-file release artifact at `artifacts\lcemcp\LceMcp.exe` after stopping stale artifact processes that held the old executable open. Release tests passed with 41 tests and the new artifact SHA256 is `9BDCAA8D51A3BBA132924885F10C9C585D4A0F4DF52C49C8A40EF2CD33A4DC6B`.
- Packaged smoke succeeded for `mcp-config --client opencode` and MCP `initialize`/`tools/list` through `artifacts\lcemcp\LceMcp.exe serve`.

Manual Yahoo body-sync performance result on 2026-06-24:

- `dotnet build lcemcp.slnx /p:UseSharedCompilation=false` succeeded before the live run.
- Live Yahoo metadata sync with `sync --account yahoo --since-days 40 --max-per-folder 0 --batch-size 50` completed in 36.758 seconds. It synced the configured 30-day scope plus 10 more days across the three enabled folders: Archive matched 0, Inbox matched/fetched 214, Sent matched/fetched 20, and 234 metadata rows were current afterward.
- The 40-day metadata run left 33 pending bodies. Live Yahoo body sync with `sync-bodies --account yahoo --max-per-folder 0 --batch-size 10` completed in 37.324 seconds: Inbox selected/fetched/persisted 33, missing 0. Final status reported 234 messages, 234 bodies, 234 search docs, and search readiness `ready`.
- Aggregate local stats for that 33-message body run: total message size about 3.2 MB, average message size about 98 KB, median 87 KB, p90 161 KB, p95 201 KB, max 418 KB, and 2 messages had attachment metadata. Stored body text was not unusually large for the elapsed time.
- Current evidence points to per-message IMAP round-trip latency and full-message MIME download as the main body-sync scaling problem. `ImapBodySync` currently calls `folder.GetMessageAsync(new UniqueId(uid))` once per pending body, which downloads a full `MimeMessage` even though the app only indexes text and recipients.
- Before changing behavior, decide whether to first add per-message/body-phase instrumentation, or directly prototype fetching only selected body sections from the already-stored body structure and batching database writes.

Body-sync performance fix on 2026-06-25:

- Changed body sync from one full `GetMessageAsync` per UID to a hybrid fetch strategy: small messages without attachments are fetched as batched full-message streams with `IImapFolder.GetStreamsAsync`, while attachment-bearing or large messages fetch only text body parts and fall back to full MIME only when no useful text part is available.
- Added batched SQLite body persistence so a sync batch commits multiple `message_bodies`/recipient/search-doc updates in one transaction while reusing the existing per-message search-index refresh logic.
- Added MCP polling guidance in initialization instructions, `email_sync_now`, `email_get_sync_status`, and active progress JSON: recommended poll interval 15 seconds, with a note that remote IMAP/body indexing is provider-paced.
- `dotnet build lcemcp.slnx /p:UseSharedCompilation=false` succeeded and `dotnet test lcemcp.slnx /p:UseSharedCompilation=false` passed with 42 tests after the fix.
- Live Yahoo metadata sync with `sync --account yahoo --since-days 50 --max-per-folder 0 --batch-size 50` completed in 50.223 seconds and created 81 pending bodies. The intermediate text-part-only implementation synced those 81 bodies in 82.139 seconds, about 1.01 seconds/message, only a modest improvement from the old 1.13 seconds/message baseline.
- Live Yahoo metadata sync with `sync --account yahoo --since-days 60 --max-per-folder 0 --batch-size 50` completed in 47.628 seconds and created 50 fresh pending bodies. The final hybrid body sync completed in 29.969 seconds, about 0.60 seconds/message. Aggregate shape for that batch: 50 messages, about 6.6 MB total, average 132 KB, median 68 KB, p90 233 KB, p95 336 KB, max 1.15 MB, 11 attachment-bearing messages, and 39 messages likely handled by batched full-stream fetch.
- Final live status after the 60-day run reported 365 messages, 365 bodies, 365 search docs, search readiness `ready`, and 0 pending bodies.

MCP folder-sync configuration result on 2026-06-25:

- Added `email_set_folder_sync` as the MCP equivalent of CLI `set-folder-sync`. It persistently updates one cached folder's `sync_enabled` setting without contacting the mail provider, deleting cached messages, or starting sync work.
- The tool schema requires `account`, `folder`, and `enabled`, and its description tells LLM clients to inspect folders with `email_list_folders` and call `email_sync_now` afterward only when the changed default scope should be indexed.
- The handler validates one configured account and one cached folder match, returns structured failed tool results for missing/ambiguous account or folder cases, rejects enabling non-selectable folders, and returns the updated folder object on success.
- Updated `SPEC.md` with the new MCP tool contract.
- `dotnet test lcemcp.slnx /p:UseSharedCompilation=false` passed with 43 tests. `scripts/build-release.ps1` also passed Release tests with 43 tests and refreshed `artifacts\lcemcp\LceMcp.exe`; new SHA256 is `0A453D56FF6F714DF0893A07402DC3EEB8A57AD028F75C123D495AB4FB83FD0E`.
- Packaged stdio smoke verified that `tools/list` from `artifacts\lcemcp\LceMcp.exe serve` advertises `email_set_folder_sync`.

Next work:

- Implement filter-only/date-only `email_search` for MCP and CLI, including cursor behavior and readiness handling.
- Fix explicit disabled-folder sync selection for MCP and CLI, then add regression tests around a disabled selectable folder such as `Important`.
- Make `email_sync_now`/`email_get_sync_status` surface zero-folder or failed one-off syncs clearly enough that LLM clients cannot treat them as successful coverage.
- Improve `email_estimate_sync` output so default selected-folder estimates are clearly distinguished from all-folder estimates.
- Consider adding provider-probe estimates to `email_estimate_sync`; the current implementation is deliberately factual but cached-count only.
- Consider a CLI or MCP folder-list view that highlights both current `sync_enabled` and role default side by side for setup UX.
- Consider adding body-sync fetch-mode counters to sync summaries/status if future tuning needs exact counts rather than the current aggregate/manual measurement.

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
