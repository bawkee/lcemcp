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
- Raw attachment file download by default.
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
3. Parse user query into a safe FTS expression.
4. Bind normal SQL criteria through SqlBinder.
5. Bind FTS expression as a parameter, not through string concatenation.
6. Execute message and/or attachment query branches.
7. Merge and rank results in app code.
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
    <account_id>\
      <message_id>\
        original\
        extracted\
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
    sync_enabled INTEGER NOT NULL DEFAULT 1,
    uidvalidity TEXT,
    last_uid INTEGER,
    last_sync_at TEXT,
    FOREIGN KEY(account_id) REFERENCES accounts(id),
    UNIQUE(account_id, path)
);
```

`role` should be a best-effort string such as `inbox`, `sent`, `archive`, `all_mail`, `drafts`, `trash`, `spam`, or `custom`. Let users override inferred roles.

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
    part_id TEXT,
    filename TEXT,
    mime_type TEXT,
    size_bytes INTEGER,
    content_hash TEXT,
    storage_path TEXT,
    download_status TEXT NOT NULL DEFAULT 'not_downloaded',
    download_attempts INTEGER NOT NULL DEFAULT 0,
    download_error TEXT,
    extraction_status TEXT NOT NULL DEFAULT 'not_ready',
    extraction_attempts INTEGER NOT NULL DEFAULT 0,
    extraction_started_at TEXT,
    extraction_lease_until TEXT,
    extraction_error TEXT,
    extracted_text_available INTEGER NOT NULL DEFAULT 0,
    ocr_text_available INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id)
);

CREATE INDEX idx_attachments_message ON attachments(message_id);
CREATE INDEX idx_attachments_hash ON attachments(content_hash);
CREATE INDEX idx_attachments_extraction_status ON attachments(extraction_status, extraction_lease_until);
```

Use status strings rather than a generic jobs table in v1. On startup, reset stale `running` extraction rows whose lease expired.

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

### 11.10 drafts

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

### 11.11 audit_log

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
    mime_type TEXT,
    extracted_text TEXT,
    FOREIGN KEY(attachment_id) REFERENCES attachments(id)
);

CREATE VIRTUAL TABLE attachments_fts USING fts5(
    filename,
    mime_type,
    extracted_text,
    content='attachment_search_docs',
    content_rowid='attachment_id',
    tokenize='unicode61 remove_diacritics 2'
);
```

Index content:

- Filename.
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

Recommended default for real use:

```toml
history_days = 1095
```

Recommended default for development/testing:

```toml
history_days = 30
```

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
9. Extract text from attachments.
10. OCR images/scanned PDFs if enabled.
11. Build/update attachment search documents and FTS.
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

The MCP server should remain available while sync is running. Tools should expose sync status and partial results.

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
mark extraction_status = pending/running/done/failed/skipped
use extraction_lease_until for running work
write extracted output to a temp path first
commit DB state and atomic file moves carefully
on startup, reset expired running rows back to pending
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
```

Milestone 3 or later:

```text
PNG
JPG/JPEG
WEBP
TIFF
scanned PDF OCR
XLSX
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

---

## 16. MCP Tool Design

Expose safe, high-level tools.

Do not expose arbitrary SQL.

Do not expose raw filesystem access.

Prefer short snake_case tool names. Dots are allowed by current MCP naming guidance, but snake_case is friendlier across clients and tool aggregators.

Recommended tool names:

```text
email_list_accounts
email_list_folders
email_search
email_get_message
email_get_thread
email_get_attachment_text
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

### 16.3 email_list_folders

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

### 16.4 email_search

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
  "include_hit_counts": true,
  "snippet_chars": 1024,
  "max_snippets_per_message": 5,
  "max_snippets_per_attachment": 5,
  "max_attachment_hits_per_message": 5,
  "limit": 20,
  "cursor": null
}
```

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
          "mime_type": "application/pdf",
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
  "has_more": false,
  "next_cursor": null
}
```

The result shape should stay message-centric even when searching attachments only. Attachment-only searches should return the parent message with matching attachment hits populated.

### 16.5 email_get_message

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
        "mime_type": "application/pdf",
        "size_bytes": 240120,
        "extracted_text_available": true
      }
    ]
  }
}
```

### 16.6 email_get_thread

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

### 16.7 email_get_attachment_text

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
    "mime_type": "application/pdf",
    "extracted_text": "Statement text...",
    "ocr_text": null,
    "combined_text_truncated": false,
    "extractor": "PdfPig",
    "extracted_at": "2026-06-17T17:22:00Z"
  }
}
```

If raw attachment access is ever added, make it a separate disabled-by-default dangerous capability.

### 16.8 email_sync_now

Manually trigger sync.

Input:

```json
{
  "accounts": ["gmail", "yahoo"],
  "full": false
}
```

Output:

```json
{
  "accepted": true,
  "sync_run_id": "sync-20260617-172500",
  "status": "running",
  "message": "Sync started for 2 accounts."
}
```

For long-running sync, return a run/status ID rather than blocking indefinitely.

### 16.9 email_get_sync_status

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
  "accounts": [
    {
      "account": "gmail",
      "enabled": true,
      "last_success_at": "2026-06-17T17:18:00Z",
      "last_error": null,
      "messages_indexed": 12043,
      "attachments_indexed": 884,
      "extraction_pending": 12,
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

### 16.10 email_get_audit_events

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

### 16.11 Future Draft Tools

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
download raw attachment file
```

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
3. Parse user query into safe FTS expression.
4. Bind optional SQL criteria through SqlBinder.
5. If search_in includes messages or is empty/null, search messages_fts.
6. If search_in includes attachments or is empty/null, search attachments_fts.
7. Join and group matches by parent message.
8. Compute estimated message hit count, attachment hit counts, and combined score.
9. Generate hit-centered message and attachment snippets.
10. Rank.
11. Return compact message-centric results with nested matching attachments.
12. AI requests full message/thread/attachment only when needed.
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
- Initial sync with configurable history_days.
- Canonical messages + message_locations schema.
- Message metadata sync.
- Message body sync.
- message_search_docs and messages_fts index.
- SqlBinder-based optional criteria for search queries.
- Hit-centered snippet extraction.
- MCP tools:
  - email_list_accounts
  - email_list_folders
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
- Attachment download policy.
- PDF embedded text extraction.
- TXT/HTML/CSV/DOCX text extraction where practical.
- attachment_search_docs and attachments_fts index.
- `email_search` supports `search_in = ["attachments"]`.
- `email_search` supports `search_in = ["messages", "attachments"]`.
- `email_search` returns nested matching attachment hits.
- `email_search` returns message/attachment hit counts when practical.
- MCP tool:
  - email_get_attachment_text
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
history_days = 1095
periodic_sync_minutes = 10
download_attachments = "small"
max_attachment_mb = 25
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
