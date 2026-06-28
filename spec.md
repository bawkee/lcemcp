# Locally-Cached Email MCP Server Specification

## 1. Executive Summary

Build a local-first email indexing service that gives AI agents fast, bounded, cited access to a large email corpus.

The service should sync mail locally, index it locally, and expose a small set of safe MCP tools. It should not try to become a smart business-document classifier. The AI client is the reasoning layer; this app is the durable email cache, search engine, and permission boundary.

Recommended foundation:

```text
C# / .NET 10 Worker or console app
MailKit + MimeKit -> IMAP, SMTP, MIME parsing
SQLite + FTS5     -> local metadata, bodies, attachment text, search indexes
SqlBinder         -> optional SQL criteria composition
MCP stdio         -> default AI-facing interface
Optional HTTP     -> future localhost admin UI and remote MCP mode
```

Core product idea:

```text
mail providers
  -> sync engine
  -> local cache/index
  -> flexible local search
  -> safe MCP tools
  -> AI agent decides what matters
```

The first useful version should not use embeddings/vector search. SQLite FTS5, metadata filters, extracted attachment text, and high-quality hit snippets should be enough to make the tool valuable.

Embeddings can be added later as an optional semantic layer after sync, search, and evidence retrieval are already reliable.

---

## 2. Product Thesis

Most email integrations are human-oriented:

```text
show a page of messages
open one message
maybe search provider-side
maybe fetch a few results
```

AI agents need something different:

```text
search across years
search across accounts
search bodies and attachments together
rank noisy results
retrieve hit-centered snippets
request full messages selectively
inspect attachment text selectively
build cited timelines
stay inside explicit permissions
```

The MCP server should not dump thousands of messages into the model. It should search locally, rank locally, and return compact evidence with stable IDs. The agent can then request full messages, threads, and attachment text only when needed.

This is an intentional security tradeoff. A local email corpus is sensitive. But local storage also allows better control, better auditability, offline search, and less provider lock-in.

---

## 3. Primary Goals

The system should allow questions like:

```text
Search all accounts for messages from GitHub support about account recovery.
Search Yahoo only for PDF attachments mentioning "refund processed".
Search Gmail and Yahoo together for emails from my accountant mentioning DDV or VAT.
Find the old thread where delivery timing changed.
Find attachments mentioning Sala Software d.o.o.
Find messages and attachments containing a specific order number.
Summarize the last conversation with a vendor, with citations.
```

The MCP should return compact, ranked, cited search results and let the AI selectively request:

```text
full message metadata
plain text body
thread context
attachment metadata
extracted attachment text
OCR text, when available
```

Search must work both per-account and across all accounts. The user should be able to ask "search Yahoo only", "search Gmail only", or "search all mail" without changing tools.

---

## 4. Non-Goals for v1

Do not implement these in v1:

- Gmail API as a required dependency.
- Microsoft Graph as a required dependency.
- Cloud sync or hosted backend.
- Public remote HTTP access.
- Web UI as the first product surface.
- Desktop GUI as the first product surface.
- Autonomous sending.
- Autonomous deletion.
- Autonomous unsubscribe.
- Raw SQL exposed to AI clients.
- Raw filesystem access exposed to AI clients.
- Raw attachment bytes, bulk export, or arbitrary-path attachment download by default.
- Embeddings/vector DB as the primary search mechanism.
- Thunderbird profile parsing as the primary backend.
- Full OCR pipeline as a required dependency.
- Built-in business-document classification.
- App-generated semantic interpretations of message meaning.

Provider-specific APIs can be optional adapters later, but the foundation should remain IMAP/SMTP where possible.

Important realism note: provider-agnostic does not mean auth-agnostic. Gmail, Yahoo, Microsoft 365, iCloud, and company mail servers all have different authentication and app-password/OAuth rules. The sync engine can be provider-agnostic. Onboarding cannot be perfectly provider-agnostic.

---

## 5. Recommended Tech Stack

Use C# / .NET 10.

The project is a long-running local cache daemon with IMAP sync, persistent state, credential handling, search, and optional future service hosting. .NET is a good fit for that center of gravity.

Default shape:

```text
.NET 10 console / Worker Service
Microsoft.Extensions.Hosting
MCP stdio transport
background sync service
SQLite local database
```

CLI commands should stay simple:

```text
lcemcp serve        # MCP stdio server, default for AI clients
lcemcp sync         # run sync now, then exit
lcemcp status       # print sync/index status
lcemcp setup        # configure account/folders
lcemcp reindex      # rebuild FTS indexes
lcemcp unlock       # future encrypted DB/session unlock helper
```

Add ASP.NET Core only when needed:

```text
lcemcp admin        # future localhost web admin UI
lcemcp serve-http   # future HTTP MCP transport, disabled by default
```

Recommended .NET libraries:

```text
ModelContextProtocol
MailKit
MimeKit
Microsoft.Data.Sqlite
SQLitePCLRaw.bundle_e_sqlite3
SqlBinder
Microsoft.Extensions.Hosting
Microsoft.Extensions.Logging
Tomlyn or similar TOML parser
```

Avoid EF Core in v1. The schema is small, query behavior matters, and FTS5 is easier to reason about with explicit SQL.

Development guidelines:

```text
Keep it pragmatic SOLID.
Prefer simple, testable boundaries over deep architecture layers.
Prefer functional style for parsing, normalization, ranking, and snippet extraction.
Keep side effects at the edges: IMAP, SMTP, SQLite, filesystem, MCP transport, logging.
Prefer small pure functions where practical.
Prefer composition over inheritance.
Avoid Clean Architecture ceremony and interface-per-class bloat.
Prefer readable code over clever code.
```

C# style:

```text
Use modern C# features where they improve clarity.
Use `var` when the right-hand side makes the type obvious.
Use target-typed `new()` when the target type is clear.
Use file-scoped namespaces.
Use implicit usings in the project file.
Disable nullable reference types in the project file.
Prefer records/record structs for simple immutable data carriers when useful.
Prefer pattern matching and collection expressions when they simplify the code.
Avoid clever LINQ chains when a short named helper is easier to read and test.
```

Recommended project defaults:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>disable</Nullable>
  <LangVersion>latest</LangVersion>
</PropertyGroup>
```

Simplicity rule:

```text
If a new abstraction does not make sync, search, storage, or MCP behavior easier to understand, do not add it.
```

Preferred extraction path:

```text
PDF text: PdfPig first
PDF fallback: optional external pdftotext/poppler
OCR: optional external Tesseract worker
Images: ImageSharp or platform image APIs if needed
DOCX: Open XML SDK or DocumentFormat.OpenXml
CSV/TXT/HTML: built-in/simple parsers
```

Keep OCR optional. Tesseract and language packs are useful, but they are not lightweight.

---

## 6. Query Construction

Use explicit SQL with parameters. Do not expose SQL to MCP clients.

SqlBinder is a good fit for the search layer because `email_search` has many optional filters:

```text
accounts
search targets
sender/recipient
date range
folder roles
attachment presence
MIME types
filename filters
limit/cursor
```

SqlBinder lets the app keep readable SQL templates while enabling optional criteria through `SetCondition` and `SetConditionRange`.

Recommended approach:

```text
1. Parse and validate MCP arguments.
2. Convert account names/emails to local account IDs.
3. If a text query is present, parse it into a safe FTS expression.
4. If no text query is present, require at least one bounded metadata criterion and run a filtered listing path.
5. Bind normal SQL criteria through SqlBinder.
6. Bind FTS expression as a parameter when FTS is used, not through string concatenation.
7. Execute message and/or attachment query branches.
8. Merge and rank results in app code.
```

Example template shape:

```sql
SELECT
    m.id,
    m.account_id,
    m.subject,
    m.from_email,
    m.date_sent
FROM messages m
{WHERE
    {m.account_id :accountIds}
    {m.from_email :fromEmail}
    {m.date_sent :dateRange}
    {m.has_attachments :hasAttachments}}
ORDER BY m.date_sent DESC
LIMIT :limit
```

The FTS query string still needs its own parser/escaper. SqlBinder solves optional SQL criteria composition; it does not make raw FTS syntax safe by itself.

---

## 7. Future UI Direction

Do not build a desktop UI first.

The right phase-2 UI is a small localhost web admin UI, not a desktop app.

Reasons:

- It works on Windows, macOS, Linux, and remote servers.
- It works through SSH port forwarding if the service is remote.
- It avoids desktop packaging, tray behavior, DPI issues, and app lifecycle complexity.
- It is ideal for account setup, OAuth redirects, folder selection, sync status, logs, audit review, and draft/send confirmation.
- It can share the same ASP.NET Core host used later for optional HTTP MCP transport.
- It keeps the main product as a service, not an email client.

Avalonia is technically viable, but it should wait until there is a concrete need for native desktop behavior.

The future local admin UI should be deliberately limited:

```text
account setup
provider presets
credential setup/unlock flow
folder selection
sync progress/status
attachment extraction status
audit log viewer
dangerous action review
draft preview/send confirmation
manual sync/reindex controls
simple search debugger
```

If database encryption is added, avoid relying on an interactive prompt inside MCP stdio startup. MCP clients may start the server headlessly and expect JSON-RPC immediately.

Better unlock options:

```text
1. OS-protected key:
   Windows DPAPI / Credential Manager stores an encryption key or unlock secret.

2. CLI unlock:
   User runs `lcemcp unlock`, enters passphrase, service stores a short-lived unlock token.

3. Local admin UI unlock:
   User opens localhost UI and unlocks the database for the current session.

4. Environment variable:
   Useful for remote/server deployments, documented as sensitive.
```

If locked, MCP tools should fail clearly with:

```text
database_locked
run `lcemcp unlock` or open the local admin UI
```

Do not let a stdio password prompt corrupt MCP protocol output.

---

## 8. Core Architecture

Default local architecture:

```text
Codex / Claude Code / other MCP client
  -> MCP stdio
  -> Email MCP worker
      -> safe MCP tools
      -> search service
      -> sync scheduler
      -> extraction worker
  -> SQLite metadata + FTS5
  -> attachment store
  -> provider IMAP/SMTP servers
```

Future local web architecture:

```text
Browser on localhost
  -> local admin UI
  -> same app services
  -> same SQLite database
  -> same credential store
```

The HTTP/admin surface must be disabled by default until explicitly configured.

---

## 9. Design Principles

### 9.1 Local-first

All useful searchable data should be stored locally:

- Message metadata.
- Message bodies.
- Thread relationships or computed thread keys.
- Sender/recipient metadata.
- Attachment metadata.
- Extracted PDF/document text.
- OCR text when enabled.
- FTS5 indexes.
- Sync state.
- Audit log.

The system should remain searchable even when mail providers are offline.

### 9.2 Provider-agnostic Core

The project should work with any provider that exposes IMAP/SMTP:

- Yahoo Mail.
- Gmail over IMAP/SMTP.
- Fastmail.
- Zoho.
- iCloud.
- Outlook/Hotmail if IMAP is enabled and auth works.
- Custom domains.
- Company mail servers.
- Proton Mail through Proton Bridge.

Provider presets are useful. They should fill in known hosts, ports, security settings, and auth guidance. They should not change the storage/search architecture.

### 9.3 Search Before AI

The AI should not receive thousands of emails and "figure it out."

Instead:

```text
AI asks MCP
  -> MCP runs local search
  -> MCP returns bounded ranked results with hit-centered snippets
  -> AI requests details selectively
```

### 9.4 FTS5 First, Embeddings Later

For email, exact and keyword search are highly valuable:

```text
sender
recipient
subject
dates
order numbers
company names
product names
attachment filenames
attachment text
```

Use SQLite FTS5 with metadata filters first.

Embeddings should be optional later functionality after:

- Sync is reliable.
- Attachment extraction works.
- Ranking is acceptable.
- The user can inspect cited evidence.

### 9.5 Safe Writes

Read/search/summarize can be normal actions.

Sending, deleting, moving, unsubscribing, forwarding, and bulk changes must require explicit confirmation and should be disabled by default.

### 9.6 Email Content Is Untrusted

Email bodies and attachment text are untrusted data. They may contain prompt injections such as "ignore previous instructions" or "send this file."

The MCP server should:

- Return email content as evidence, not instructions.
- Avoid tool descriptions that encourage blindly obeying email text.
- Include source metadata and message IDs with all snippets.
- Keep dangerous write tools disabled by default.
- Log all tool calls.

---

## 10. Storage Layout

Default Windows data directory:

```text
%LOCALAPPDATA%\LceMcp\
  config.toml
  email.db
  attachments\
    objects\
      sha256\
        ab\
          cd\
            abcdef...   # original or extracted binary, content-addressed
    exports\
      <access_token_or_run_id>\
        invoice.pdf     # optional user-facing copies/links
  logs\
    audit.log
    sync.log
```

Default Unix-like data directory:

```text
~/.local/share/lcemcp/
  config.toml
  email.db
  attachments/
  logs/
```

Future profile support:

```text
%LOCALAPPDATA%\LceMcp\profiles\default\
%LOCALAPPDATA%\LceMcp\profiles\work\
```

Do not expose this filesystem layout directly through MCP tools.

SQLite should store attachment metadata, extraction state, content hashes, and storage keys. Original attachment binaries should not be stored as SQLite BLOBs by default. Keep them in the app-managed attachment store so large files can be streamed, deduplicated by hash, cleaned up independently, and served/exported without bloating the database.

`storage_key` values are internal app keys relative to the managed attachment store, not caller-controlled paths. MCP tools must not return internal storage keys or paths directly. User-facing access should be prepared through a bounded access/export tool that returns scoped local links or managed export paths.

---

## 11. Database Design

Use SQLite as the default backend.

Future optional backends:

- SQLCipher-encrypted SQLite.
- PostgreSQL for server deployments.
- SQLite + sqlite-vec/sqlite-vss for optional semantic search.
- PostgreSQL + pgvector for larger hosted deployments.

Use explicit migrations:

```sql
CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at TEXT NOT NULL
);
```

### 11.1 accounts

```sql
CREATE TABLE accounts (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    email_address TEXT NOT NULL,
    provider_preset TEXT,
    imap_host TEXT NOT NULL,
    imap_port INTEGER NOT NULL,
    imap_security TEXT NOT NULL DEFAULT 'ssl',
    smtp_host TEXT,
    smtp_port INTEGER,
    smtp_security TEXT,
    username TEXT NOT NULL,
    auth_type TEXT NOT NULL,
    credential_ref TEXT,
    created_at TEXT NOT NULL,
    last_sync_at TEXT,
    enabled INTEGER NOT NULL DEFAULT 1
);
```

Do not store plaintext passwords in this table.

### 11.2 folders

```sql
CREATE TABLE folders (
    id INTEGER PRIMARY KEY,
    account_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    path TEXT NOT NULL,
    delimiter TEXT,
    role TEXT,
    sync_enabled INTEGER NOT NULL DEFAULT 0,
    uidvalidity TEXT,
    last_uid INTEGER,
    last_sync_at TEXT,
    FOREIGN KEY(account_id) REFERENCES accounts(id),
    UNIQUE(account_id, path)
);
```

`role` should be a best-effort string such as `inbox`, `sent`, `archive`, `all_mail`, `drafts`, `trash`, `spam`, or `custom`. Let users override inferred roles.

Folder discovery should not silently make every selectable folder syncable. On first discovery, use role-based defaults:

```text
sync_enabled default true: inbox, sent, archive, all_mail
sync_enabled default false: spam, junk, bulk, trash, deleted, drafts, outbox, custom/unknown
```

Rediscovery must preserve existing user choices for known folders. New folders may receive role-based defaults, but discovery should report enough folder metadata for the user or LLM harness to choose deliberately.

### 11.3 messages

`messages` is the canonical message table. It should not be keyed by folder UID because labels and IMAP copies can cause the same logical message to appear in multiple folders.

```sql
CREATE TABLE messages (
    id INTEGER PRIMARY KEY,
    account_id INTEGER NOT NULL,
    provider_message_key TEXT,
    provider_thread_key TEXT,
    message_id_header TEXT,
    in_reply_to TEXT,
    references_header TEXT,
    thread_key TEXT,
    subject TEXT,
    normalized_subject TEXT,
    from_name TEXT,
    from_email TEXT,
    date_sent TEXT,
    date_received TEXT,
    has_attachments INTEGER NOT NULL DEFAULT 0,
    size_bytes INTEGER,
    raw_headers TEXT,
    body_downloaded INTEGER NOT NULL DEFAULT 0,
    attachments_scanned INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(account_id) REFERENCES accounts(id)
);

CREATE INDEX idx_messages_account_date ON messages(account_id, date_sent);
CREATE INDEX idx_messages_provider_key ON messages(account_id, provider_message_key);
CREATE INDEX idx_messages_message_id_header ON messages(account_id, message_id_header);
CREATE INDEX idx_messages_thread_key ON messages(account_id, thread_key);
```

Deduplication should use a best-effort key:

```text
provider-specific stable ID if available
Message-ID header
normalized subject + sender + date proximity fallback
```

Do not assume every email has a valid `Message-ID`.

### 11.4 message_locations

`message_locations` maps a canonical message to one or more provider folders and UIDs.

```sql
CREATE TABLE message_locations (
    id INTEGER PRIMARY KEY,
    message_id INTEGER NOT NULL,
    account_id INTEGER NOT NULL,
    folder_id INTEGER NOT NULL,
    provider_uid TEXT NOT NULL,
    provider_message_key TEXT,
    flags TEXT,
    labels TEXT,
    deleted_locally INTEGER NOT NULL DEFAULT 0,
    expunged INTEGER NOT NULL DEFAULT 0,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id),
    FOREIGN KEY(account_id) REFERENCES accounts(id),
    FOREIGN KEY(folder_id) REFERENCES folders(id),
    UNIQUE(account_id, folder_id, provider_uid)
);

CREATE INDEX idx_message_locations_message ON message_locations(message_id);
CREATE INDEX idx_message_locations_folder_uid ON message_locations(folder_id, provider_uid);
```

Folder-specific flags belong here, not on the canonical message.

### 11.5 message_bodies

```sql
CREATE TABLE message_bodies (
    message_id INTEGER PRIMARY KEY,
    plain_text TEXT,
    html_text TEXT,
    normalized_text TEXT,
    detected_language TEXT,
    normalized_at TEXT,
    FOREIGN KEY(message_id) REFERENCES messages(id)
);
```

HTML-to-text normalization matters. Prefer readable body text over exact visual fidelity.

### 11.6 message_recipients

```sql
CREATE TABLE message_recipients (
    id INTEGER PRIMARY KEY,
    message_id INTEGER NOT NULL,
    type TEXT NOT NULL,
    name TEXT,
    email TEXT,
    FOREIGN KEY(message_id) REFERENCES messages(id)
);
```

`type` should be a simple string such as `to`, `cc`, `bcc`, or `reply_to`.

### 11.7 attachments

```sql
CREATE TABLE attachments (
    id INTEGER PRIMARY KEY,
    message_id INTEGER NOT NULL,
    parent_attachment_id INTEGER,
    root_attachment_id INTEGER,
    source_kind TEXT NOT NULL DEFAULT 'email_part',
    part_id TEXT,
    filename TEXT,
    display_path TEXT,
    archive_entry_path TEXT,
    mime_type TEXT,
    sniffed_mime_type TEXT,
    size_bytes INTEGER,
    compressed_size_bytes INTEGER,
    uncompressed_size_bytes INTEGER,
    content_hash TEXT,
    storage_key TEXT,
    is_container INTEGER NOT NULL DEFAULT 0,
    nesting_depth INTEGER NOT NULL DEFAULT 0,
    download_status TEXT NOT NULL DEFAULT 'not_downloaded',
    download_attempts INTEGER NOT NULL DEFAULT 0,
    download_started_at TEXT,
    download_lease_until TEXT,
    download_next_attempt_at TEXT,
    download_completed_at TEXT,
    download_error_code TEXT,
    download_error TEXT,
    extraction_status TEXT NOT NULL DEFAULT 'not_ready',
    extraction_attempts INTEGER NOT NULL DEFAULT 0,
    extraction_started_at TEXT,
    extraction_lease_until TEXT,
    extraction_next_attempt_at TEXT,
    extraction_completed_at TEXT,
    extraction_error_code TEXT,
    extraction_error TEXT,
    extractor TEXT,
    extractor_version TEXT,
    extracted_text_available INTEGER NOT NULL DEFAULT 0,
    ocr_text_available INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id),
    FOREIGN KEY(parent_attachment_id) REFERENCES attachments(id),
    FOREIGN KEY(root_attachment_id) REFERENCES attachments(id)
);

CREATE INDEX idx_attachments_message ON attachments(message_id);
CREATE INDEX idx_attachments_parent ON attachments(parent_attachment_id);
CREATE INDEX idx_attachments_root ON attachments(root_attachment_id);
CREATE INDEX idx_attachments_hash ON attachments(content_hash);
CREATE INDEX idx_attachments_display_path ON attachments(message_id, display_path);
CREATE INDEX idx_attachments_extraction_status ON attachments(extraction_status, extraction_lease_until);
```

Use status strings rather than a generic jobs table in v1. On startup, reset stale `running` extraction rows whose lease expired.

Attachment rows are a document tree:

- Top-level email MIME parts use `source_kind = email_part`, no parent, and a `part_id`.
- Files discovered inside archives use `source_kind = archive_entry`, point to `parent_attachment_id`, inherit `message_id`, and set `archive_entry_path`.
- `display_path` is the user/LLM-facing path, for example `invoice.pdf` or `bundle.zip!/invoices/openai.pdf`.
- `root_attachment_id` points at the top-level email attachment when practical. It may be null during insertion and backfilled after the root row exists.
- `is_container = 1` means the attachment can contain child documents, such as ZIP or 7z.
- `nesting_depth` starts at 0 for top-level email parts and increments for archive children.
- `mime_type` is provider/MIME-declared; `sniffed_mime_type` is the app's best-effort type from file signatures/content.
- `storage_key` is an internal key into the managed attachment object store, not a raw path exposed to MCP clients.

Use one row for every searchable or user-accessible binary. A ZIP attachment gets its own row, and each supported file inside it gets a child row. DOCX and XLSX are ZIP-based internally, but they are terminal document formats for this model; do not expose their internal Open XML package files as child attachments except in debug tooling.

Recommended status values:

```text
download_status: not_downloaded | pending | running | retry_wait | stored | skipped | failed
extraction_status: not_ready | pending | running | retry_wait | done | empty | failed
```

`messages.attachments_scanned = 1` means the message's MIME/body structure was successfully enumerated and expected top-level attachment rows were recorded. It does not mean every attachment binary downloaded or extracted successfully. Download and extraction failures remain visible and retryable through attachment rows without repeatedly downloading the message body.

Unsupported types, encrypted documents, corrupt documents, safety-limit rejections, timeouts, unavailable workers, and unexpected extractor exceptions all use `extraction_status = failed` plus a stable domain `extraction_error_code`. Do not persist CLR exception class names as the public error code. Example codes:

```text
unsupported_attachment_type
invalid_document
encrypted_document
attachment_too_large
archive_safety_limit
extractor_timeout
extractor_unavailable
temporary_io_failure
worker_crashed
unknown_extractor_failure
```

The current attachment row is the latest-state projection. Keep attempt history and deduplicated failure issues separately:

```sql
CREATE TABLE attachment_extraction_attempts (
    id INTEGER PRIMARY KEY,
    attachment_id INTEGER NOT NULL,
    trigger_kind TEXT NOT NULL,
    client_name TEXT,
    extractor TEXT,
    extractor_version TEXT,
    started_at TEXT NOT NULL,
    completed_at TEXT,
    outcome TEXT NOT NULL,
    error_code TEXT,
    exception_type TEXT,
    exception_message TEXT,
    exception_details TEXT,
    FOREIGN KEY(attachment_id) REFERENCES attachments(id) ON DELETE CASCADE
);

CREATE TABLE attachment_extraction_failures (
    id INTEGER PRIMARY KEY,
    attachment_id INTEGER NOT NULL,
    stage TEXT NOT NULL,
    error_code TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'open',
    first_attempt_id INTEGER NOT NULL,
    latest_attempt_id INTEGER NOT NULL,
    occurrence_count INTEGER NOT NULL DEFAULT 1,
    first_seen_at TEXT NOT NULL,
    last_checked_at TEXT NOT NULL,
    resolved_at TEXT,
    resolved_by_attempt_id INTEGER,
    FOREIGN KEY(attachment_id) REFERENCES attachments(id) ON DELETE CASCADE,
    FOREIGN KEY(first_attempt_id) REFERENCES attachment_extraction_attempts(id),
    FOREIGN KEY(latest_attempt_id) REFERENCES attachment_extraction_attempts(id),
    FOREIGN KEY(resolved_by_attempt_id) REFERENCES attachment_extraction_attempts(id)
);

CREATE UNIQUE INDEX idx_attachment_extraction_failures_open
ON attachment_extraction_failures(attachment_id, stage, error_code)
WHERE status = 'open';
```

`trigger_kind` should distinguish at least `initial`, `automatic_retry`, `explicit_retry`, and `extractor_upgrade`. Each execution creates an attempt row. Repeating the same failure updates the existing open failure issue's `latest_attempt_id`, `last_checked_at`, and `occurrence_count`; it must not create another open issue. A successful extraction marks all open issues for that attachment/stage `resolved` and records `resolved_by_attempt_id`. If the error classification changes without successful extraction, mark the old issue `superseded` and open the new issue rather than calling the old one fixed.

Record actual exception diagnostics for failed attempts:

- Stable domain error code.
- Runtime exception type.
- Exception message.
- Full exception details, including stack trace and inner exceptions.
- Extractor name/version, attachment ID, stage, trigger kind, and timestamps.

Write full `Exception.ToString()`-style diagnostics to rotating local logs on stderr-compatible logging paths. Keep a reasonably bounded copy in SQLite for durable troubleshooting. Do not log raw attachment bytes or extracted document text as exception context. Normal MCP responses should expose stable error codes, safe summaries, and bounded metadata, not full stack traces or internal filesystem paths.

Expected capability outcomes such as `unsupported_attachment_type` do not need a synthetic runtime exception. Their exception fields may be null while the stable error code and safe explanation remain populated. When an extractor actually throws, preserve the real exception details rather than replacing them with only the domain code.

### 11.8 attachment_text

```sql
CREATE TABLE attachment_text (
    attachment_id INTEGER PRIMARY KEY,
    extracted_text TEXT,
    ocr_text TEXT,
    combined_text TEXT,
    extractor TEXT,
    extracted_at TEXT,
    FOREIGN KEY(attachment_id) REFERENCES attachments(id)
);
```

### 11.9 sync_state

```sql
CREATE TABLE sync_state (
    id INTEGER PRIMARY KEY,
    account_id INTEGER NOT NULL,
    folder_id INTEGER NOT NULL,
    state_json TEXT,
    last_success_at TEXT,
    last_error_at TEXT,
    last_error TEXT,
    FOREIGN KEY(account_id) REFERENCES accounts(id),
    FOREIGN KEY(folder_id) REFERENCES folders(id),
    UNIQUE(account_id, folder_id)
);
```

### 11.10 sync_runs and sync_leases

`sync_runs` is durable user-visible job history. It is not itself the concurrency primitive.

```sql
CREATE TABLE sync_runs (
    id TEXT PRIMARY KEY,
    scope_key TEXT NOT NULL DEFAULT 'global',
    account_id INTEGER,
    account_name TEXT NOT NULL,
    folder_filter TEXT,
    owner_id TEXT,
    status TEXT NOT NULL,
    phase TEXT NOT NULL,
    done INTEGER NOT NULL DEFAULT 0,
    total INTEGER NOT NULL DEFAULT 0,
    requested_at TEXT NOT NULL,
    started_at TEXT NOT NULL,
    last_progress_at TEXT NOT NULL,
    completed_at TEXT,
    last_error TEXT,
    FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE SET NULL
);

CREATE INDEX idx_sync_runs_scope_status ON sync_runs(scope_key, status, requested_at);
CREATE INDEX idx_sync_runs_status_progress ON sync_runs(status, last_progress_at);
CREATE INDEX idx_sync_runs_account_progress ON sync_runs(account_id, last_progress_at);
```

`sync_leases` is the authority for who may perform sync work. For v1, use a single global sync scope so only one sync operation runs at a time. Parallel per-account sync can be added later by changing the scope key without changing the client-facing MCP contract.

```sql
CREATE TABLE sync_leases (
    scope_key TEXT PRIMARY KEY,
    sync_run_id TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    heartbeat_at TEXT NOT NULL,
    lease_expires_at TEXT NOT NULL,
    FOREIGN KEY(sync_run_id) REFERENCES sync_runs(id) ON DELETE CASCADE
);

CREATE INDEX idx_sync_leases_expires ON sync_leases(lease_expires_at);
```

### 11.11 drafts

Drafts are not required in v1.

```sql
CREATE TABLE drafts (
    id INTEGER PRIMARY KEY,
    account_id INTEGER NOT NULL,
    related_message_id INTEGER,
    to_json TEXT NOT NULL,
    cc_json TEXT,
    bcc_json TEXT,
    subject TEXT NOT NULL,
    body_text TEXT NOT NULL,
    body_html TEXT,
    status TEXT NOT NULL DEFAULT 'draft',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(account_id) REFERENCES accounts(id),
    FOREIGN KEY(related_message_id) REFERENCES messages(id)
);
```

### 11.12 audit_log

```sql
CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY,
    timestamp TEXT NOT NULL,
    client_name TEXT,
    tool_name TEXT NOT NULL,
    action_type TEXT NOT NULL,
    arguments_summary TEXT,
    affected_message_ids TEXT,
    affected_attachment_ids TEXT,
    affected_draft_ids TEXT,
    result_summary TEXT
);
```

Log every MCP call, including read/search calls.

---

## 12. FTS5 Indexes

Use external-content FTS5 tables. The app maintains plain search-document tables and FTS virtual tables index those documents.

### 12.1 Why Search Document Tables Exist

The source-of-truth tables are normalized:

```text
messages
message_bodies
message_recipients
attachments
attachment_text
```

FTS works best when each indexed row contains one searchable document. The search-document tables are denormalized, rebuildable projections used only for search.

They solve a few problems:

- One row per message for message search.
- One row per attachment for attachment search.
- Recipients and body text can be flattened into a single indexed row.
- FTS tables can use stable row IDs.
- Reindexing is simple: rebuild docs from source-of-truth tables, then rebuild FTS.
- Query output can cleanly join search hits back to messages and attachments.

These tables should not be treated as canonical storage.

### 12.2 Message Search Documents

```sql
CREATE TABLE message_search_docs (
    message_id INTEGER PRIMARY KEY,
    subject TEXT,
    from_email TEXT,
    from_name TEXT,
    recipients TEXT,
    body TEXT,
    FOREIGN KEY(message_id) REFERENCES messages(id)
);

CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject,
    from_email,
    from_name,
    recipients,
    body,
    content='message_search_docs',
    content_rowid='message_id',
    tokenize='unicode61 remove_diacritics 2'
);
```

Index content:

- Subject.
- From email.
- From name.
- To/CC/BCC emails and names.
- Normalized message body.
- Useful header snippets if needed.

### 12.3 Attachment Search Documents

```sql
CREATE TABLE attachment_search_docs (
    attachment_id INTEGER PRIMARY KEY,
    filename TEXT,
    display_path TEXT,
    mime_type TEXT,
    extracted_text TEXT,
    FOREIGN KEY(attachment_id) REFERENCES attachments(id)
);

CREATE VIRTUAL TABLE attachments_fts USING fts5(
    filename,
    display_path,
    mime_type,
    extracted_text,
    content='attachment_search_docs',
    content_rowid='attachment_id',
    tokenize='unicode61 remove_diacritics 2'
);
```

Index content:

- Filename.
- Display path, including archive paths such as `bundle.zip!/invoice.pdf`.
- MIME type.
- Extracted PDF/document text.
- OCR text when available.

### 12.4 FTS Safety

Do not pass raw user/AI query text directly into FTS syntax without parsing/escaping.

The search layer should support:

```text
plain terms
quoted phrases
AND/OR behavior controlled by code
prefix search only where deliberately enabled
metadata filters outside FTS
```

Malformed FTS syntax should return a friendly validation error, not crash the tool call.

### 12.5 Ranking

Search should rank using:

```text
BM25 score
date proximity
sender match
subject match
attachment match
folder role preference
thread density
estimated or exact hit count
```

Attachment matches should not be hidden behind message-only results. If a hit is inside an attachment, the result should show the matching attachment and hit-centered snippets from that attachment.

### 12.6 Hit Counts

The unified `email_search` tool should try to report hit counts separately for message text and attachment text.

Desired counts:

```text
message_hit_count
attachment_hit_count
per_attachment_hit_count
total_hit_count
hit_count_mode
```

Estimated counts are acceptable. The purpose is to help agents rank noisy results, not to provide legal-grade occurrence accounting.

FTS5 provides ranking and snippets directly through built-in auxiliary functions such as `bm25`, `highlight`, and `snippet`. Exact per-row occurrence details are available through the FTS5 extension API (`xInstCount`/`xInst`), but exact counts should not block v1.

Use:

```text
hit_count_mode = "estimated"
```

until an exact implementation exists.

---

## 13. Snippet Design

Search snippets must be hit-centered, not just the first N characters of the message.

Input controls:

```json
{
  "snippet_chars": 1024,
  "max_snippets_per_message": 5,
  "max_snippets_per_attachment": 5
}
```

Defaults:

```text
snippet_chars = 1024
max_snippets_per_message = 5
max_snippets_per_attachment = 5
```

Recommended bounds:

```text
snippet_chars minimum = 160
snippet_chars maximum = 4096
```

Output should use arrays:

```json
{
  "message_snippets": [
    {
      "field": "body",
      "text": "...hit-centered context..."
    }
  ],
  "matching_attachments": [
    {
      "attachment_id": 987,
      "snippets": [
        {
          "field": "extracted_text",
          "text": "...hit-centered context..."
        }
      ]
    }
  ]
}
```

Implementation guidance:

```text
1. Use SQLite FTS5 snippet/highlight when it produces useful hit-centered output.
2. If SQLite returns too little context or only one fragment, fetch the normalized source text.
3. Locate parsed search terms/phrases in app code.
4. Extract configurable context windows around each hit.
5. Merge overlapping windows.
6. Cap total snippets per message/attachment.
```

This matters because email is noisy. If the search term appears near the end of a long message, returning the beginning of the message is actively misleading to the agent.

---

## 14. Sync Engine

### 14.1 Account Setup

During setup, prompt user for:

```text
Account display name
Email address
Provider preset
IMAP host/port/security
SMTP host/port/security, optional until sending is enabled
Username
Authentication method
History days
Folders to sync
Attachment download policy
```

Use `history_days` instead of a fixed enum.

Examples:

```toml
history_days = 30
history_days = 365
history_days = 1095
history_days = null # everything available
```

Recommended default for first-run real use:

```toml
history_days = 90
```

Recommended default for development/testing:

```toml
history_days = 30
```

`history_days` is the default requested sync/search window stored in `config.toml`; it is not a hard cap and it should not be silently rewritten by MCP tools. A user or local admin UI may change it deliberately, and CLI setup may accept an explicit `--history-days`, but MCP sync calls should use per-call `since_days` overrides for one-off wider backfills.

Offer larger values such as 365 days, 1095 days, or everything available as deliberate choices rather than silent defaults.

Attachment policies should be simple strings:

```text
metadata_only
download_small_attachments
download_all_attachments
download_on_demand
```

Recommended default:

```text
download_small_attachments
```

with configurable limit, e.g. 25 MB.

Attachment download policy controls whether the app stores original attachment binaries for extraction and later user access. It should not mean "return raw bytes to the MCP client." Binaries are stored in the managed attachment object store, with SQLite as the authority for metadata and status.

Container attachments, such as ZIP and 7z, should follow the same policy as other attachments. If a container is downloaded and within safety limits, the extraction worker may expand it into child attachment rows and store supported child binaries in the same managed object store.

### 14.2 Initial Sync

Initial sync should happen in phases:

```text
1. Discover folders.
2. Store folder metadata and roles.
3. Sync message envelopes/metadata.
4. Build canonical messages and message_locations.
5. Sync message bodies.
6. Build/update message search documents and FTS.
7. Download attachment metadata.
8. Download attachments according to policy.
9. Expand supported archive/container attachments into child attachment rows.
10. Extract text from attachments and supported child documents.
11. OCR images/scanned PDFs if enabled.
12. Build/update attachment search documents and FTS.
```

This allows useful search before all attachments are processed.

### 14.3 Incremental Sync

On startup:

```text
1. Open local DB.
2. Start MCP server.
3. Start background sync scheduler.
4. Connect to enabled accounts.
5. Check folder UIDVALIDITY.
6. Fetch new UIDs.
7. Update flags/location state.
8. Detect expunged/deleted messages where possible.
9. Mark new attachments for extraction when applicable.
10. Refresh search documents and FTS indexes.
```

While running:

```text
Periodic sync every 5-15 minutes.
Optional IMAP IDLE support later.
Manual sync through email_sync_now.
```

The MCP server should remain available while sync is running. Tools should expose sync status and durable progress. Search tools must not quietly return partial search results by default.

#### 14.3.1 Sync Window Semantics

Treat `history_days` as the configured default requested window. `email_sync_now.since_days` and CLI `--since-days` are per-run overrides and must not update `config.toml`.

For each account/folder sync scope, compute an effective metadata sync window:

```text
requested_since_days =
  full or since_days = 0 -> 0, meaning no date bound
  explicit since_days    -> explicit value
  otherwise              -> account history_days from config.toml

gap_since_days =
  days since the latest successful uncapped metadata sync for the same account/folder,
  plus a small overlap such as 1-2 days

effective_since_days =
  requested_since_days = 0 -> 0
  no prior successful uncapped sync -> requested_since_days
  otherwise -> max(requested_since_days, gap_since_days)
```

This prevents holes when an account was last synced farther back than the current default history window. For example, if `history_days = 90` but the last successful sync was 150 days ago, a default sync should use an effective window of about 150 days, not only 90 days. If the user explicitly requests 365 days, the effective window should remain at least 365 days.

Capped runs (`max_per_folder > 0` where not all matches were selected) must not count as closing a coverage gap. Sync output and status should expose `requested_since_days`, `effective_since_days`, and `auto_expanded_for_gap` so LLM clients can explain what happened without inferring from raw timestamps.

#### 14.3.2 Search Readiness

Email search readiness is binary for the requested scope.

Default behavior:

```text
requested search scope fully indexed -> run search and return results
requested search scope not fully indexed -> return not_synced/readiness status, not an empty result set
```

The requested scope includes:

- Account selection.
- Folder selection or folder roles.
- Configured or explicitly requested searchable history window.
- Message metadata sync.
- Message body sync.
- Message search documents and FTS index rows.

For v1, message search should not rely on partial body indexing. A search result of zero must mean "the fully indexed corpus was searched and nothing matched." It must not mean "the body index is incomplete."

If a future debug or advanced mode supports partial search, it must be explicit, for example `allow_partial: true`, and the response must clearly say results are incomplete. Do not enable partial search by default for MCP clients.

Attachment search readiness can be tracked separately from message search readiness. Message search may be ready before attachment extraction is complete, as long as the response is explicit about which corpus was searched.

#### 14.3.3 Long-Running Sync Progress

Long-running sync should be durable and observable.

`email_sync_now` should start or resume a sync run and return quickly with a `sync_run_id`. LLM clients can then poll `email_get_sync_status` instead of blocking indefinitely.

Progress should include:

```text
sync_run_id
status: queued | running | succeeded | failed | canceled
phase: discovering_folders | syncing_metadata | syncing_bodies | indexing | extracting_attachments
done
total
percent
elapsed_seconds
estimated_remaining_seconds
estimate_confidence: low | medium | high
last_progress_at
last_error
```

When an MCP client provides a progress token and the server stack supports progress notifications, the server may emit progress notifications during long tool calls. Pollable sync status remains the primary contract because not every LLM harness surfaces progress notifications consistently.

ETA values are best-effort. They should be calculated from observed progress over time and should expose low confidence early in a run or when provider throttling/body sizes make throughput unstable.

#### 14.3.4 Sync Queue And Lease Ownership

MCP stdio clients commonly launch the server as a subprocess. Multiple harnesses, windows, or chat sessions may therefore create multiple local server processes that point at the same SQLite database and provider accounts. The sync engine must not rely on there being only one process.

For v1, sync coordination should be deliberately conservative:

```text
one global sync scope
one active sync lease at a time
queued sync runs wait behind the active lease
heartbeat-based crash recovery
owner-checked progress/completion
```

`sync_runs` records user-visible work and progress. It may contain `queued`, `running`, `succeeded`, `failed`, and `canceled` rows. A `queued` or `running` row alone does not prove ownership.

`sync_leases` is the authority. A process may perform sync work only while it owns an unexpired lease for the relevant scope. The first implementation should use a single `global` scope because safety and user experience matter more than parallelism. Later, per-account scopes may be introduced if provider behavior and DB contention are understood.

Lease acquisition must be atomic:

```text
1. Open SQLite with a busy timeout.
2. Start an immediate write transaction.
3. Mark expired leases as failed and release them.
4. If an unexpired lease exists, enqueue or return the existing queued/running run.
5. If no lease exists, claim the oldest queued run or create a new running run.
6. Insert/update the lease with owner_id, heartbeat_at, and lease_expires_at.
7. Commit.
```

The owner should heartbeat the lease independently from user-visible progress. A slow IMAP call may delay progress counts, but liveness should be based on the lease heartbeat rather than `sync_runs.last_progress_at`.

Completion and cancellation must be owner-checked. A process that lost its lease due to expiry must not later mark the run succeeded or overwrite a newer owner's state. Stale owners should be allowed to finish their in-memory cleanup, but database completion should be ignored unless the lease still matches the same `sync_run_id` and `owner_id`.

Crash recovery should operate on leases, not on progress rows. If a process dies, its heartbeat stops; after the lease expires, another process may mark that run failed and claim the next queued run. Do not infer that a live run crashed merely because progress counts have not changed recently.

`email_sync_now` should not block indefinitely. If it starts work, it returns the running run ID. If another process already owns the lease, it returns the active or queued run ID plus progress/status so the client can poll `email_get_sync_status`.

#### 14.3.5 LLM Client Guidance

Tool descriptions and MCP documentation should instruct LLM clients to:

```text
1. Call email_get_setup_status before onboarding or when account prerequisites may be missing.
2. Call email_get_sync_status before trusting email_search for a new or recently changed account.
3. If search_ready is false, call email_sync_now or report that indexing is still required.
4. Poll email_get_sync_status at reasonable intervals, such as 10 seconds.
5. Use progress percent, elapsed time, and estimated remaining time to decide whether to wait or tell the user sync is still running.
6. Treat not_synced search responses as "not ready", never as evidence that no matching email exists.
7. Treat history_days from config.toml as the default requested window. Use email_sync_now.since_days for one-off wider syncs instead of changing config.
```

### 14.4 Crash Safety

Avoid a generic jobs table in v1.

For sync:

```text
commit progress in small batches
only advance sync_state after a batch succeeds
make message upserts idempotent
derive missing work from durable DB state
```

For attachment extraction:

```text
use attachment rows as the work source
mark extraction_status = pending/running/retry_wait/done/empty/failed
use extraction_lease_until for running work
record every execution attempt and deduplicate open failure issues
write extracted output through bounded temporary storage
commit DB state and atomic file moves carefully
on startup, classify expired running leases and return retryable work to retry_wait
```

For archive/container extraction:

```text
create child attachment rows idempotently from the parent attachment row
preserve archive entry filenames through display_path and archive_entry_path
sanitize entry paths and reject path traversal
enforce depth, entry count, per-entry size, total uncompressed size, compression-ratio, and timeout limits
mark encrypted, too-large, unsupported, and failed entries explicitly
write child binaries through the same content-addressed object store
do not expose archive extraction temp paths through MCP
```

If a generic work queue is added later, it must have leases, attempts, heartbeats, retry limits, and startup recovery. Do not create fire-and-forget work records that can be abandoned forever after a crash.

### 14.5 Provider Quirks

Gmail IMAP:

- Labels appear as folders.
- Same logical message can appear in multiple folders.
- `[Gmail]/All Mail` may be the most complete folder.
- Deduplicate through provider stable IDs when available, then Message-ID fallback.
- Avoid double-counting search results.

Yahoo IMAP:

- App password may be required.
- Folder naming may be inconsistent.
- Be conservative about special folder assumptions.

Microsoft 365 / Outlook:

- Basic auth may not work.
- OAuth2 support is likely required for many accounts.
- IMAP/SMTP OAuth onboarding is more complex than plain app-password setup.

Custom IMAP:

- Do not assume folder names.
- Infer common roles, but let user override.

Proton Mail:

- Treat Proton Bridge as the local IMAP/SMTP endpoint.
- Do not try to implement Proton's proprietary service directly in v1.

### 14.6 Per-Message Failure Isolation

Failures at account or folder boundaries may fail the applicable sync scope:

```text
authentication failure
connection failure
folder open failure
folder-level summary/list command failure
sync lease loss or cancellation
```

Once individual message targets are known, failures attributable to one message or attachment must be isolated from independent targets. IMAP part download errors, MIME decode errors, malformed message content, object-store write errors, and attachment processor failures for one target must not abort the rest of the folder.

Required behavior:

```text
1. Process and persist successful messages independently of failed siblings in the same batch.
2. Record the failed message/attachment, stable error code, safe summary, and full local exception diagnostics.
3. Increment attempted/progress counts and continue with later targets.
4. Leave retryable work pending or in retry_wait with bounded backoff.
5. Surface aggregate failures in the folder/run result rather than reporting an unqualified success.
6. Re-throw cancellation and lease-loss exceptions immediately.
```

A repeatedly failing newest message must not starve older targets, including when `max_per_folder` is bounded. Target selection should prioritize never-attempted work and due retries, and exclude backoff-delayed rows until `next_attempt_at`. Exhausted terminal failures remain visible but do not monopolize every subsequent sync.

### 14.7 Attachment Download Size Enforcement

The attachment size policy is a download boundary, not merely a post-download storage check.

Required behavior:

```text
1. Persist the top-level attachment metadata row from body structure before requesting its binary.
2. Inspect provider/body-structure size metadata before requesting a part.
3. If a trustworthy declared size exceeds the configured limit, record attachment_too_large and do not request the binary.
4. Treat provider sizes as advisory; enforce the same limit while decoding/streaming.
5. Stop after at most limit + 1 decoded bytes, clean partial temporary files, and do not retain a partial object.
6. Stream decoded content through incremental hashing into the managed object store instead of materializing arbitrarily large byte arrays in memory.
7. Apply cancellation and a bounded timeout during download/decode.
```

The hard cap applies to decoded attachment bytes. Transfer encoding, incorrect provider metadata, missing `Octets`, and full-message fallback paths must not bypass it. Archive expansion has separate compressed/uncompressed limits in addition to the top-level binary download limit.

---

## 15. Attachment Extraction and OCR

### 15.1 Supported Formats by Milestone

Milestone 1:

```text
attachment metadata only
optional TXT/HTML extraction if trivial
```

Milestone 2:

```text
PDF embedded text
TXT
HTML
CSV
DOCX
XLSX
ZIP archive expansion
7z archive expansion where practical
```

Milestone 3 or later:

```text
PNG
JPG/JPEG
WEBP
TIFF
scanned PDF OCR
RAR and other archive formats if there is a safe, maintained local extractor
```

### 15.2 PDF Processing

For each PDF:

```text
1. Try embedded text extraction.
2. If text is empty, tiny, or suspicious, mark as OCR candidate.
3. If OCR is enabled, render pages to images.
4. OCR rendered pages.
5. Store extracted text, OCR text, and combined text.
6. Update attachment search documents and FTS.
```

Preferred implementation order:

```text
PdfPig first
optional external pdftotext/poppler fallback
optional Tesseract OCR
```

### 15.3 Image OCR

For image attachments:

```text
1. Normalize image.
2. Run OCR if enabled.
3. Store OCR text.
4. Update attachment search documents and FTS.
```

OCR should be disabled or optional by default until the rest of the system is reliable.

### 15.4 Archive and Nested Document Processing

Archive processing should feed the same attachment, extraction, readiness, search, snippet, and user-access paths as top-level attachments.

For each supported archive/container:

```text
1. Store or verify the parent container binary according to attachment policy.
2. Open the container with bounded limits and a timeout.
3. Enumerate entries without trusting entry paths.
4. Create one child attachment row per supported file entry.
5. Store child binaries in the managed object store when policy and limits allow.
6. Recurse into supported nested containers until max depth or another safety limit is reached.
7. Run the normal extractor registry for terminal child documents.
8. Update attachment search documents and FTS for every extracted child.
```

Safety limits should be explicit config values with conservative defaults:

```text
max_archive_depth
max_archive_entries
max_archive_entry_mb
max_archive_total_uncompressed_mb
max_archive_compression_ratio
archive_extraction_timeout_seconds
```

Entry-count, total-uncompressed-size, and timeout budgets apply to the complete
recursively expanded tree rooted at one email attachment. Nested containers must
share the root budget rather than resetting these limits at each ZIP level.

Archive entries must never write directly to caller-controlled paths. Normalize separators, reject absolute paths and `..` traversal, preserve the original entry name for display, and expose a safe `display_path` such as `statements.zip!/2026-06/statement.pdf`.

DOCX and XLSX are Open XML documents and should be handled by document extractors before generic archive expansion. Their internal ZIP entries are implementation detail, not user-facing child attachments.

### 15.5 Failure Classification and Retry

Attachment download/enumeration and local text extraction are separate lifecycle stages. Once a binary is stored in the managed object store, extraction retries must use that local object and must not re-download it from IMAP.

Automatic retry policy is bounded and error-code-specific:

```text
invalid/unsupported/encrypted/safety-limit failures -> no automatic retry
timeout/unavailable/temporary I/O/worker crash       -> at most 3 total attempts with backoff
unknown extractor failure                            -> one delayed automatic retry, then terminal
```

`retry_wait` rows become claimable only after `extraction_next_attempt_at`. Claim work transactionally by moving one row to `running`, incrementing its attempt count, and setting a lease. Expired `running` leases become retryable failures without allowing two workers to process the same attachment concurrently.

Explicit user or LLM retries are allowed for any open extraction failure, including `unsupported_attachment_type`. An explicit retry grants one additional attempt beyond the automatic retry budget; it does not reset an unlimited automatic loop. If the same failure recurs, record the attempt and diagnostics but reuse the existing open failure issue. If extraction succeeds, update attachment text/search documents and mark the issue resolved/fixed.

When extractor support or versions change, the app may offer or schedule retries filtered by error code, MIME type, and prior extractor version. This uses the same explicit/upgrade attempt path rather than a special `force` mode.

Readiness semantics:

- `pending`, `running`, and `retry_wait` mean attachment extraction is not complete for the requested scope.
- Terminal open failures do not block the rest of the searchable corpus forever.
- Status/readiness must report open failure counts and an error-code breakdown so `attachment_search_index_complete = true` cannot be mistaken for “every attachment produced text.”
- Successful retry resolves the issue and updates text coverage without changing the stable attachment ID.

---

## 16. MCP Tool Design

Expose safe, high-level tools.

Do not expose arbitrary SQL.

Do not expose raw filesystem access.

Prefer short snake_case tool names. Dots are allowed by current MCP naming guidance, but snake_case is friendlier across clients and tool aggregators.

Recommended tool names:

```text
email_list_accounts
email_get_setup_status
email_discover_folders
email_estimate_sync
email_list_folders
email_set_folder_sync
email_search
email_get_message
email_get_thread
email_get_attachment_text
email_prepare_attachment_access
email_list_attachment_extraction_failures
email_retry_attachment_extraction
email_sync_now
email_get_sync_status
email_get_audit_events
email_create_draft
email_preview_draft
email_send_draft
```

Draft/send tools are later-phase tools and disabled by default.

### 16.1 Common Search Rules

All search-style behavior should:

- Return stable local IDs.
- Return account/folder/date context.
- Return hit-centered snippets.
- Include score/rank values.
- Include `truncated` where output is clipped.
- Include `has_more` or cursor when more results exist.
- Avoid returning full bodies unless specifically requested.

Account filtering rules:

```text
accounts omitted -> search all enabled accounts
accounts null    -> search all enabled accounts
accounts []      -> search all enabled accounts
accounts list    -> search only matching account names/IDs/email addresses
```

`search_in` should be a list, not a magic single value:

```json
"search_in": ["messages", "attachments"]
```

Rules:

```text
search_in omitted -> search all searchable content
search_in null    -> search all searchable content
search_in []      -> search all searchable content
```

### 16.2 email_list_accounts

List configured accounts.

Input:

```json
{
  "enabled_only": true
}
```

Output:

```json
{
  "accounts": [
    {
      "account_id": 1,
      "name": "yahoo",
      "email_address": "user@yahoo.com",
      "provider_preset": "yahoo",
      "enabled": true,
      "last_sync_at": "2026-06-17T17:20:00Z"
    },
    {
      "account_id": 2,
      "name": "gmail",
      "email_address": "user@gmail.com",
      "provider_preset": "gmail",
      "enabled": true,
      "last_sync_at": "2026-06-17T17:18:00Z"
    }
  ]
}
```

### 16.3 email_get_setup_status

Return local setup prerequisites without making network calls. This tool should not report sync progress or search readiness; those belong to `email_get_sync_status`.

Top-level statuses:

```text
no_accounts
needs_attention
setup_complete
```

Per-account setup statuses:

```text
config_invalid
credential_missing
folders_not_discovered
setup_complete
```

Output:

```json
{
  "status": "needs_attention",
  "accounts": [
    {
      "account": "yahoo",
      "setup_status": "folders_not_discovered",
      "config_valid": true,
      "credential_status": "present",
      "folders_cached": false,
      "default_history_days": 90,
      "default_history_source": "config.toml"
    }
  ]
}
```

### 16.4 email_discover_folders

Connect to the provider, discover folders, persist local folder metadata, and return folder roles/counts/default sync choices. It must not sync messages.

Input:

```json
{
  "accounts": ["yahoo"]
}
```

Output:

```json
{
  "status": "succeeded",
  "folders": [
    {
      "folder_id": 10,
      "account": "yahoo",
      "path": "Inbox",
      "role": "inbox",
      "selectable": true,
      "message_count": 10000,
      "sync_enabled": true,
      "default_sync_enabled": true
    },
    {
      "folder_id": 11,
      "account": "yahoo",
      "path": "Trash",
      "role": "trash",
      "selectable": true,
      "message_count": 3200,
      "sync_enabled": false,
      "default_sync_enabled": false
    }
  ]
}
```

Rediscovery must preserve existing sync choices for known folders.

### 16.5 email_estimate_sync

Return factual sync estimates for the current or proposed sync scope. Do not include warning prose; LLM clients can decide how to present counts to users.

Depending on setup state, this can return one of:

```text
needs_account
config_invalid
credential_missing
folders_not_discovered
estimated
failed
```

Input:

```json
{
  "accounts": ["yahoo"],
  "folders": ["Inbox", "Sent"],
  "since_days": 365,
  "probe": true
}
```

Output:

```json
{
  "status": "estimated",
  "estimate_source": "provider_probe",
  "requested_since_days": 365,
  "effective_since_days": 365,
  "selected_folder_count": 2,
  "total_estimated_messages": 14400,
  "estimate_confidence": "medium",
  "folders": [
    {
      "path": "Inbox",
      "role": "inbox",
      "estimated_messages": 12000
    },
    {
      "path": "Sent",
      "role": "sent",
      "estimated_messages": 2400
    }
  ]
}
```

`estimate_source` may be `cached_folder_counts`, `provider_probe`, or `mixed`.

### 16.6 email_list_folders

List known folders.

Input:

```json
{
  "accounts": ["gmail"],
  "sync_enabled_only": false
}
```

Output:

```json
{
  "folders": [
    {
      "folder_id": 10,
      "account": "gmail",
      "name": "All Mail",
      "path": "[Gmail]/All Mail",
      "role": "all_mail",
      "sync_enabled": true,
      "last_sync_at": "2026-06-17T17:18:00Z"
    }
  ]
}
```

### 16.6.1 email_set_folder_sync

Persistently enable or disable one cached folder in the default sync scope.

This is the MCP equivalent of `set-folder-sync`. It only updates local folder configuration. It must not contact the provider, delete cached messages, or start sync work. Agents should use `email_list_folders` to inspect folder paths/names/ids before changing a folder, and call `email_sync_now` afterward if the changed default scope should be indexed immediately.

Input:

```json
{
  "account": "yahoo",
  "folder": "Archive",
  "enabled": false
}
```

Output:

```json
{
  "status": "updated",
  "updated": true,
  "account": "yahoo",
  "sync_enabled": false,
  "folder": {
    "folder_id": 10,
    "account": "yahoo",
    "path": "Archive",
    "role": "archive",
    "selectable": true,
    "sync_enabled": false
  },
  "message": "Folder 'Archive' is now excluded from future default email_sync_now runs. Existing cached mail was not deleted."
}
```

### 16.7 email_search

Unified search across message metadata, message bodies, and/or extracted attachment text.

This is the main retrieval tool. It must be flexible enough for an agent to search:

```text
messages only
attachments only
messages and attachments together
one account
all enabled accounts
specific date/sender/folder/file criteria
```

Input:

```json
{
  "query": "\"refund processed\" OR chargeback",
  "accounts": null,
  "search_in": ["messages", "attachments"],
  "from_email": null,
  "to_email": null,
  "date_from": "2025-01-01",
  "date_to": "2026-12-31",
  "has_attachment": true,
  "folder_roles": ["inbox", "sent", "archive", "all_mail"],
  "mime_types": ["application/pdf"],
  "filename_contains": null,
  "include_attachment_metadata": true,
  "include_hit_counts": true,
  "snippet_chars": 1024,
  "max_snippets_per_message": 5,
  "max_snippets_per_attachment": 5,
  "max_attachment_hits_per_message": 5,
  "allow_partial": false,
  "limit": 20,
  "cursor": null
}
```

`query` may be null, omitted, or blank when the caller supplies bounded metadata criteria such as account, date range, sender, recipient, folder role, attachment, MIME type, or filename filters. This is the same tool an agent should use for date-only or filter-only browsing, such as "show Yahoo messages from June" or "list recent sent mail with attachments." The server should reject an unbounded all-mail listing request, but it should not force callers to invent broad synthetic text terms just to use date or folder filters.

When no text query is supplied, skip FTS `MATCH`, apply the same readiness rules for the requested scope, and return deterministic newest-first results, for example by `COALESCE(date_sent, date_received) DESC, message_id DESC`. Hit counts and hit-centered snippets are not applicable in filter-only mode; return ordinary bounded metadata/body previews only when they are explicitly part of the response contract.

Output:

```json
{
  "results": [
    {
      "message_id": 123,
      "account": "gmail",
      "folders": ["All Mail"],
      "date": "2025-11-03",
      "from": "support@example.com",
      "subject": "Re: chargeback follow-up",
      "message_hit_count": 2,
      "attachment_hit_count": 4,
      "total_hit_count": 6,
      "hit_count_mode": "estimated",
      "message_snippets": [
        {
          "field": "body",
          "text": "...we reviewed the case and the refund processed on..."
        },
        {
          "field": "subject",
          "text": "Re: chargeback follow-up"
        }
      ],
      "has_attachments": true,
      "matching_attachments": [
        {
          "attachment_id": 987,
          "filename": "statement.pdf",
          "display_path": "statements.zip!/statement.pdf",
          "mime_type": "application/pdf",
          "size_bytes": 240120,
          "source_kind": "archive_entry",
          "parent_attachment_id": 456,
          "extracted_text_available": true,
          "access_preparable": true,
          "hit_count": 4,
          "snippets": [
            {
              "field": "extracted_text",
              "text": "...transaction status: refund processed..."
            }
          ]
        }
      ],
      "score": 12.31
    }
  ],
  "freshness": {
    "source": "local_cache",
    "response_generated_at": "2026-06-24T20:05:30Z",
    "search_scope_as_of": "2026-06-24T18:50:54Z",
    "last_sync_performed_at": "2026-06-24T18:51:21Z",
    "oldest_scoped_sync_at": "2026-06-24T18:50:54Z",
    "newest_scoped_sync_at": "2026-06-24T18:51:21Z",
    "cache_age_seconds": 4476,
    "requested_date_from": "2026-06-22T00:00:00Z",
    "requested_date_to": "2026-06-24T23:59:59Z",
    "requested_upper_bound": "2026-06-24T23:59:59Z",
    "requested_range_extends_beyond_cache": true
  },
  "has_more": false,
  "next_cursor": null
}
```

The result shape should stay message-centric even when searching attachments only. Attachment-only searches should return the parent message with matching attachment hits populated.

`matching_attachments` should include attachments that matched attachment text, filename, MIME type, or attachment-only filter criteria. In filter-only mode, for example `has_attachment = true` or `filename_contains = "statement"`, return bounded attachment metadata even when there are no text snippets. If the query only filters by `has_attachment = true`, return a compact attachment preview up to `max_attachment_hits_per_message` so the agent can inspect filenames without fetching the full message.

For archive children, return the child attachment ID and `display_path`. The parent archive may also be returned when it matched by filename or text, but child document hits should not be hidden behind the archive row.

Search does not need to create downloadable links as a side effect. It should return `access_preparable = true` when the original binary or child binary can be prepared for user access through `email_prepare_attachment_access`.

`email_search` readiness and freshness are separate. `search_ready = true` means the local index is complete for the requested searchable scope. It does not mean the local cache reflects the provider at response time. Every search response should include `freshness` so LLM clients can decide whether the cached view is current enough for the user's question. For multi-folder or multi-account scopes, `search_scope_as_of` is the oldest successful sync timestamp among the scoped folders; `last_sync_performed_at` is the newest. A query about a fully historical range may tolerate an older `search_scope_as_of`, while a query about "today", "this week", or other current mail usually should sync when `requested_range_extends_beyond_cache` is true.

Default readiness rule:

```text
email_search must not return ordinary empty results when the requested searchable corpus is not fully indexed.
```

If the requested scope is not ready, return a readiness response instead of `results: []`:

```json
{
  "status": "not_synced",
  "search_ready": false,
  "message": "The requested email search corpus is not fully indexed.",
  "sync_run_id": "sync-20260617-172500",
  "readiness": {
    "scope": {
      "accounts": ["yahoo"],
      "folder_roles": ["inbox"],
      "history_days": 90,
      "search_in": ["messages"]
    },
    "metadata_complete": true,
    "bodies_complete": false,
    "message_search_index_complete": false,
    "attachments_complete": null,
    "indexed_messages": 500,
    "metadata_messages": 5000,
    "pending_message_bodies": 4500
  },
  "progress": {
    "phase": "syncing_bodies",
    "done": 500,
    "total": 5000,
    "percent": 10,
    "elapsed_seconds": 60,
    "estimated_remaining_seconds": 540,
    "estimate_confidence": "low"
  }
}
```

`allow_partial` defaults to false. If it is ever implemented, partial search must be opt-in and the response must include:

```json
{
  "status": "partial",
  "search_ready": false,
  "results_may_be_incomplete": true
}
```

### 16.8 email_get_message

Fetch one message with metadata, optional body, and optional attachment metadata.

Input:

```json
{
  "message_id": 123,
  "include_body": true,
  "include_attachments": true,
  "max_body_chars": 20000
}
```

Output:

```json
{
  "message": {
    "message_id": 123,
    "account": "gmail",
    "folders": ["All Mail"],
    "date_sent": "2025-11-03T12:30:00Z",
    "from": {
      "name": "Example Support",
      "email": "support@example.com"
    },
    "to": [
      {
        "name": "User",
        "email": "user@gmail.com"
      }
    ],
    "subject": "Re: chargeback follow-up",
    "body_text": "Hello...",
    "body_truncated": false,
    "attachments": [
      {
        "attachment_id": 987,
        "filename": "statement.pdf",
        "display_path": "statements.zip!/statement.pdf",
        "mime_type": "application/pdf",
        "size_bytes": 240120,
        "source_kind": "archive_entry",
        "parent_attachment_id": 456,
        "is_container": false,
        "extraction_status": "done",
        "extracted_text_available": true,
        "access_preparable": true
      }
    ]
  }
}
```

### 16.9 email_get_thread

Fetch thread context around a message.

Input:

```json
{
  "message_id": 123,
  "limit": 30,
  "include_bodies": true,
  "max_body_chars_per_message": 12000
}
```

Output:

```json
{
  "thread": {
    "thread_key": "local-thread-abc",
    "threading_confidence": "best_effort",
    "messages": [
      {
        "message_id": 120,
        "date_sent": "2025-11-01T09:00:00Z",
        "from": "user@gmail.com",
        "subject": "chargeback follow-up",
        "body_text": "Hello...",
        "body_truncated": false
      },
      {
        "message_id": 123,
        "date_sent": "2025-11-03T12:30:00Z",
        "from": "support@example.com",
        "subject": "Re: chargeback follow-up",
        "body_text": "Hello...",
        "body_truncated": false
      }
    ]
  }
}
```

Threading should be best-effort and clearly marked as such if confidence is low.

### 16.10 email_get_attachment_text

Fetch extracted/OCR text for one attachment.

Input:

```json
{
  "attachment_id": 987,
  "max_chars": 50000
}
```

Output:

```json
{
  "attachment": {
    "attachment_id": 987,
    "message_id": 123,
    "filename": "statement.pdf",
    "display_path": "statements.zip!/statement.pdf",
    "mime_type": "application/pdf",
    "source_kind": "archive_entry",
    "parent_attachment_id": 456,
    "extracted_text": "Statement text...",
    "ocr_text": null,
    "combined_text_truncated": false,
    "extractor": "PdfPig",
    "extracted_at": "2026-06-17T17:22:00Z"
  }
}
```

### 16.11 email_prepare_attachment_access

Prepare user-facing access links or managed export files for known attachment IDs.

This tool is for user convenience, not for LLM binary inspection. It must not return raw bytes, base64 content, arbitrary filesystem access, or internal attachment-store paths. It may return scoped localhost URLs when the local admin/access server is available, or sanitized files copied into an app-managed export directory.

Input:

```json
{
  "attachment_ids": [987, 988],
  "access_kind": "auto",
  "expires_minutes": 60
}
```

`access_kind` values:

```text
auto
localhost_url
managed_export_file
```

Output:

```json
{
  "attachments": [
    {
      "attachment_id": 987,
      "message_id": 123,
      "filename": "openai-invoice.pdf",
      "display_path": "invoices.zip!/openai-invoice.pdf",
      "mime_type": "application/pdf",
      "size_bytes": 240120,
      "access": {
        "kind": "localhost_url",
        "url": "http://127.0.0.1:8765/attachments/tokens/abc123/openai-invoice.pdf",
        "expires_at": "2026-06-26T15:30:00Z"
      }
    }
  ]
}
```

If localhost access is not available, a managed export-file response is acceptable:

```json
{
  "access": {
    "kind": "managed_export_file",
    "path": "C:\\Users\\User\\AppData\\Local\\LceMcp\\attachments\\exports\\abc123\\openai-invoice.pdf",
    "expires_at": null
  }
}
```

Rules:

- Accept only stable local `attachment_id` values already present in SQLite.
- Never accept caller-supplied output paths.
- Sanitize filenames, avoid collisions, and preserve extensions where safe.
- Scope localhost tokens to specific attachment IDs and short expirations.
- Audit every prepared access event with attachment IDs.
- For archive children, prepare the child file itself when stored; otherwise return a clear status explaining whether the parent archive must be downloaded/extracted first.
- If the original binary is not stored yet and policy allows on-demand download, the tool may queue or perform bounded download/extraction work and return a pending/not-ready result.
- If access-link permission is disabled, fail clearly rather than returning storage paths.

Raw attachment export remains a separate dangerous capability. It would mean returning raw bytes/base64 through MCP, bulk exporting without specific attachment IDs, or writing to arbitrary caller-supplied paths. Keep that disabled by default.

### 16.11.1 email_list_attachment_extraction_failures

List bounded, unresolved or historical attachment extraction issues without exposing raw attachment content, object-store paths, or full exception stack traces.

Input:

```json
{
  "attachment_ids": null,
  "accounts": ["yahoo"],
  "error_codes": ["unsupported_attachment_type"],
  "status": "open",
  "limit": 20
}
```

Output should include stable attachment/message IDs, display path, MIME type, error code, safe error summary, runtime exception type when available, extractor/version, occurrence count, first/last timestamps, and issue status. Full exception details remain available in local diagnostic logs and durable internal attempt history, not ordinary MCP output.

### 16.11.2 email_retry_attachment_extraction

Explicitly queue one additional local extraction attempt for known failures. This tool never accepts filesystem paths and does not contact IMAP when the managed object is present.

Input:

```json
{
  "attachment_ids": null,
  "accounts": ["yahoo"],
  "error_codes": ["unsupported_attachment_type"],
  "limit": 20
}
```

Rules:

- Require explicit attachment IDs or at least one bounded failure filter such as account plus error code.
- Cap each request, for example at 50 attachments.
- Select only existing attachment rows with open failures and stored binaries.
- Refuse or report attachments already pending/running rather than queueing duplicates.
- Each selected attachment receives one explicit attempt beyond its automatic budget; there is no `force` flag.
- Repeating the same failure reuses the existing issue while recording the new attempt and exception diagnostics.
- Success updates attachment text/FTS and resolves the issue as fixed.
- Audit the tool call, selected attachment IDs, filter summary, and aggregate result.
- Return a run/status ID for potentially long work such as OCR and expose progress through the normal sync/status surface.

### 16.12 email_sync_now

Manually trigger sync.

Input:

```json
{
  "accounts": ["gmail", "yahoo"],
  "folder": null,
  "full": false,
  "since_days": 365,
  "max_per_folder": 0,
  "wait_for_completion": false
}
```

Output:

```json
{
  "accepted": true,
  "sync_run_id": "sync-20260617-172500",
  "status": "running",
  "requested_since_days": 365,
  "effective_since_days": 365,
  "auto_expanded_for_gap": false,
  "message": "Sync started for 2 accounts.",
  "progress": {
    "phase": "syncing_bodies",
    "done": 500,
    "total": 5000,
    "percent": 10,
    "elapsed_seconds": 60,
    "estimated_remaining_seconds": 540,
    "estimate_confidence": "low"
  }
}
```

If the requested/default window would leave a gap since the latest successful uncapped sync, the server should widen the effective window and report it:

```json
{
  "accepted": true,
  "sync_run_id": "sync-20260617-172500",
  "status": "running",
  "requested_since_days": 90,
  "effective_since_days": 151,
  "auto_expanded_for_gap": true
}
```

`email_sync_now` must not rewrite `history_days` in `config.toml`. A wider `since_days` is a per-run request.

When `folder` is omitted, sync the account's selectable folders with `sync_enabled = true`. When `folder` is provided, treat it as an explicit one-off request for the matching selectable folder path/name/id, even if that folder is normally `sync_enabled = false`. If the folder does not exist or is not selectable, reject the call immediately with a clear `accepted = false` response. Do not enqueue a successful-looking zero-folder run.

If another process already owns the sync lease, the tool should return the active or queued run rather than starting duplicate provider work:

```json
{
  "accepted": true,
  "sync_run_id": "sync-20260617-172500",
  "status": "queued",
  "active_sync_run_id": "sync-20260617-172100",
  "message": "A sync is already running; this request is queued.",
  "progress": {
    "phase": "syncing_metadata",
    "done": 3,
    "total": 16,
    "percent": 19
  }
}
```

For long-running sync, return a run/status ID rather than blocking indefinitely by default. `wait_for_completion` may be supported for short runs or interactive clients, but the server must still emit or expose progress and must respect cancellation when supported by the host.

`email_sync_now` should bring the requested corpus to search-ready state. For message search this means metadata, bodies, search docs, and FTS rows are complete for the requested or default history window, after automatic gap expansion. Attachment extraction may remain a separate phase unless the request explicitly includes attachment search readiness.

### 16.13 email_get_sync_status

Return account/folder sync state.

Input:

```json
{
  "accounts": null,
  "include_folders": true
}
```

Output:

```json
{
  "database_locked": false,
  "active_sync_run_id": "sync-20260617-172500",
  "accounts": [
    {
      "account": "gmail",
      "enabled": true,
      "last_success_at": "2026-06-17T17:18:00Z",
      "last_error": null,
      "search_ready": true,
      "search_ready_scope": {
        "history_days": 90,
        "history_source": "config.toml",
        "message_search_ready": true,
        "attachment_search_ready": false
      },
      "sync_progress": {
        "status": "running",
        "phase": "syncing_bodies",
        "requested_since_days": 90,
        "effective_since_days": 151,
        "auto_expanded_for_gap": true,
        "done": 500,
        "total": 5000,
        "percent": 10,
        "elapsed_seconds": 60,
        "estimated_remaining_seconds": 540,
        "estimate_confidence": "low",
        "last_progress_at": "2026-06-17T17:18:45Z"
      },
      "metadata_messages": 12043,
      "message_bodies_indexed": 12043,
      "messages_indexed": 12043,
      "attachments_indexed": 884,
      "extraction_pending": 12,
      "extraction_failures_open": 4,
      "extraction_failures_by_code": {
        "unsupported_attachment_type": 3,
        "invalid_document": 1
      },
      "folders": [
        {
          "folder": "All Mail",
          "role": "all_mail",
          "last_sync_at": "2026-06-17T17:18:00Z"
        }
      ]
    }
  ]
}
```

`email_get_sync_status` is the authoritative readiness endpoint for LLM clients. It must make the distinction between synced and not synced visible without requiring the model to infer it from raw counts.

### 16.14 email_get_audit_events

Return recent audit log entries.

Input:

```json
{
  "date_from": "2026-06-17T00:00:00Z",
  "date_to": null,
  "tool_names": ["email_search"],
  "limit": 50
}
```

Output:

```json
{
  "events": [
    {
      "timestamp": "2026-06-17T17:25:00Z",
      "client_name": "Codex",
      "tool_name": "email_search",
      "action_type": "read",
      "arguments_summary": "query length 32, all accounts, messages+attachments",
      "result_summary": "20 results, has_more=false"
    }
  ]
}
```

### 16.15 Future Draft Tools

These are not v1.

#### email_create_draft

Input:

```json
{
  "account": "gmail",
  "to": ["person@example.com"],
  "cc": [],
  "bcc": [],
  "subject": "Re: Support follow-up",
  "body_text": "Hello...",
  "related_message_id": 123
}
```

Output:

```json
{
  "draft_id": 55,
  "status": "draft",
  "requires_user_review": true
}
```

#### email_preview_draft

Input:

```json
{
  "draft_id": 55
}
```

Output:

```json
{
  "draft": {
    "draft_id": 55,
    "from": "user@gmail.com",
    "to": ["person@example.com"],
    "cc": [],
    "bcc": [],
    "subject": "Re: Support follow-up",
    "body_text": "Hello...",
    "status": "draft"
  }
}
```

#### email_send_draft

Input:

```json
{
  "draft_id": 55,
  "confirmation": "SEND DRAFT 55"
}
```

Output:

```json
{
  "sent": true,
  "draft_id": 55,
  "sent_message_id": 456
}
```

If confirmation is missing or wrong, refuse.

---

## 17. Safety and Permissions

Default config:

```toml
[permissions]
read_only = true
allow_drafts = false
allow_send = false
allow_delete = false
allow_move = false
allow_unsubscribe = false
allow_bulk_actions = false
allow_attachment_access_links = true
allow_raw_attachment_export = false
```

Drafts can be enabled before send is enabled.

The following should be disabled unless explicitly enabled:

```text
send
delete
move
archive
bulk label/folder changes
unsubscribe
forward
raw/bulk attachment export
arbitrary-path attachment download
```

Scoped, app-managed attachment access links are read-only user convenience. They should still be bounded and audited, but they are not the same as raw attachment export because they require specific known attachment IDs and do not return bytes or accept arbitrary output paths.

For sending:

```text
Show:
- From
- To
- CC/BCC
- Subject
- Body
- Attachments
- Related thread

Require explicit confirmation.
```

Recommended confirmation format:

```text
SEND DRAFT <draft_id>
```

For deletion/bulk actions:

```text
DELETE <n> MESSAGES
ARCHIVE <n> MESSAGES
UNSUBSCRIBE <sender/domain>
```

Log every MCP call.

At minimum:

```text
timestamp
client name
tool name
arguments summary
message IDs accessed
attachment IDs accessed
draft IDs created/sent
result summary
```

---

## 18. Search Flow

For a normal query:

```text
1. Validate tool arguments.
2. Parse explicit filters, including account scope and search_in list.
3. If text query is present, parse it into safe FTS expression.
4. If text query is absent, validate that the metadata filters bound the request enough for a bounded listing.
5. Bind optional SQL criteria through SqlBinder.
6. If FTS is used and search_in includes messages or is empty/null, search messages_fts.
7. If FTS is used and search_in includes attachments or is empty/null, search attachments_fts.
8. If FTS is not used, run the filtered message/attachment listing query directly.
9. Join and group matches by parent message.
10. Compute estimated message hit count, attachment hit counts, and combined score when FTS is used.
11. Generate hit-centered message and attachment snippets when FTS is used.
12. Rank FTS results by score, and order filter-only results deterministically by message date and ID.
13. Return compact message-centric results with nested matching attachments.
14. AI requests full message/thread/attachment only when needed.
```

For timeline or dispute-style queries:

```text
1. Search messages and attachments.
2. Return candidate threads/messages.
3. AI calls email_get_thread selectively.
4. AI summarizes timeline with citations.
```

---

## 19. Security Model

### 19.1 Local-only by Default

The server should not send data anywhere except:

```text
IMAP server
SMTP server, only if sending is enabled
MCP client over local stdio
optional localhost admin UI
```

No telemetry.

No analytics.

No remote logging.

### 19.2 Credential Storage

Do not store plaintext passwords if avoidable.

Preferred:

```text
Windows Credential Manager / DPAPI
macOS Keychain
Linux Secret Service / libsecret
```

Development fallback:

```text
.env
config.local.toml
```

but mark it as insecure and keep it out of source control.

### 19.3 Database Encryption

v1 may rely on OS user permissions and full disk encryption.

Recommended later:

```text
SQLCipher support
per-profile encryption
OS-protected key storage
manual passphrase unlock
```

Encryption at rest does not protect data from an already-unlocked MCP server. Permissions, tool design, audit logs, and safe defaults still matter.

### 19.4 HTTP/Admin Security

If local HTTP/admin UI is added:

```text
bind to 127.0.0.1 by default
validate Origin header
use CSRF protection
require a random local session token or login
never bind to 0.0.0.0 without explicit config
disable remote mode by default
```

If remote HTTP MCP mode is added:

```text
require TLS
require authentication
support explicit allowlist
log all client identities
document the risk clearly
```

### 19.5 Process Isolation

Recommended for sensitive users:

```text
Run MCP server as separate OS user.
Restrict filesystem access.
No arbitrary shell tools.
No raw file tool.
No remote MCP transport by default.
```

---

## 20. Implementation Roadmap

### Milestone 0: Walking Skeleton

Deliver:

```text
- .NET console/worker project.
- MCP stdio server starts cleanly.
- SQLite database initialization.
- Config file loading.
- email_get_sync_status returns useful empty-state info.
- Structured logging to stderr for MCP compatibility.
```

### Milestone 1: Read-only Local Search

Deliver:

```text
- One or more IMAP accounts.
- Folder discovery.
- Initial sync with configurable `history_days`, explicit per-run `since_days`, and automatic gap expansion through `effective_since_days`.
- Canonical messages + message_locations schema.
- Message metadata sync.
- Message body sync.
- message_search_docs and messages_fts index.
- SqlBinder-based optional criteria for search queries.
- Filter-only/date-only `email_search` browsing without requiring synthetic text query terms.
- Hit-centered snippet extraction.
- Binary search readiness for the requested scope: synced or not_synced, with no quiet partial search by default.
- Durable sync run/status tracking with progress, elapsed time, ETA, and estimate confidence.
- LLM guidance for polling sync status before trusting search on a new or incomplete corpus.
- MCP tools:
  - email_list_accounts
  - email_get_setup_status
  - email_discover_folders
  - email_estimate_sync
  - email_list_folders
  - email_set_folder_sync
  - email_search
  - email_get_message
  - email_get_thread
  - email_get_sync_status
  - email_sync_now
```

### Milestone 2: Attachments and Extracted Text

Deliver:

```text
- Attachment metadata sync.
- Attachment download policy and managed file-backed binary store.
- Recursive attachment/document tree for archive children.
- PDF embedded text extraction.
- TXT/HTML/CSV/DOCX/XLSX text extraction where practical.
- ZIP/7z archive expansion with bounded safety limits.
- Durable extraction attempts, deduplicated failure issues, bounded automatic retries, and explicit ID/error-code-filtered retry.
- attachment_search_docs and attachments_fts index.
- `email_search` supports `search_in = ["attachments"]`.
- `email_search` supports `search_in = ["messages", "attachments"]`.
- `email_search` returns nested matching attachment hits.
- `email_search` returns message/attachment hit counts when practical.
- MCP tools:
  - email_get_attachment_text
  - email_prepare_attachment_access
  - email_list_attachment_extraction_failures
  - email_retry_attachment_extraction
```

Do not require OCR yet.

### Milestone 3: Local Admin UI and Unlock Flow

Deliver:

```text
- Optional localhost web admin UI.
- Account setup flow.
- Folder selection.
- Sync status/progress.
- Audit log viewer.
- Manual sync/reindex controls.
- Optional encrypted DB unlock flow.
```

This should be a small admin surface, not a full email client.

### Milestone 4: Drafting and Sending

Deliver:

```text
- SMTP config.
- Local drafts table.
- Create draft.
- Preview draft.
- Send draft with explicit confirmation.
- Sent-message handling.
- Audit log for all write actions.
```

Sending remains disabled by default.

### Milestone 5: Hardening

Deliver:

```text
- Credential store integration.
- Better sync scheduler.
- Gmail IMAP deduplication using provider-specific IDs when available.
- Yahoo provider preset.
- Microsoft OAuth provider preset.
- Custom provider preset.
- SQLCipher optional DB encryption.
- Better ranking.
- More robust HTML-to-text normalization.
- Safer HTTP/admin mode.
- Optional OCR.
```

### Milestone 6: Optional Semantic Search

Only after FTS5 version is useful.

Deliver:

```text
- Optional embeddings.
- Local embedding model or configurable embedding provider.
- Vector index.
- Semantic reranking.
- Similar-thread search.
```

---

## 21. Recommended Defaults

```toml
[sync]
history_days = 90
periodic_sync_minutes = 10
download_attachments = "small"
max_attachment_mb = 25
max_archive_depth = 3
max_archive_entries = 500
max_archive_entry_mb = 25
max_archive_total_uncompressed_mb = 250
max_archive_compression_ratio = 100
archive_extraction_timeout_seconds = 60
ocr_enabled = false

[search]
use_fts5 = true
use_embeddings = false
default_limit = 20
max_limit = 100
snippet_chars = 1024
max_snippets_per_message = 5
max_snippets_per_attachment = 5

[permissions]
read_only = true
allow_drafts = false
allow_send = false
allow_delete = false
allow_move = false
allow_unsubscribe = false
allow_bulk_actions = false
allow_attachment_access_links = true
allow_raw_attachment_export = false

[security]
local_only = true
telemetry = false
audit_log = true
encrypt_db = false

[admin_ui]
enabled = false
bind = "127.0.0.1"
port = 8765
```

---

## 22. Example Workflows

### 22.1 Search All Accounts

User:

```text
Search all mail for messages mentioning account recovery from GitHub support.
```

MCP strategy:

```text
- Call email_search with accounts null.
- Use search_in ["messages", "attachments"] unless the user narrows it.
- Return ranked message-centric results.
- AI requests full threads only for likely matches.
```

### 22.2 Search One Account

User:

```text
Search Yahoo only for PDFs mentioning "refund processed".
```

MCP strategy:

```text
- Call email_search with accounts ["yahoo"].
- Use search_in ["attachments"].
- Filter mime_types ["application/pdf"].
- Return parent messages and matching attachment snippets.
```

### 22.3 Build a Timeline

User:

```text
Find where delivery timing changed and summarize the timeline.
```

MCP strategy:

```text
- Call email_search across messages and attachments.
- Review hit-centered snippets.
- Fetch likely threads with email_get_thread.
- Summarize with citations to message IDs and dates.
```

---

## 23. Final Recommendation

Build the system as:

```text
.NET 10 console/worker service
+ MailKit/MimeKit IMAP sync
+ local SQLite database
+ FTS5 search indexes
+ SqlBinder-backed optional SQL criteria
+ hit-centered snippets
+ optional attachment text extraction
+ constrained MCP stdio tools
+ optional localhost admin UI later
```

The smallest useful version is a read-only local email search MCP with reliable sync, flexible search, good snippets, and careful result bounding. Everything else should be layered on only after that foundation works.
