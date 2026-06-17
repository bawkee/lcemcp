# SqlBinder Skill

Use this repo-local skill note before SQL-related work, especially when editing SQLite query construction, SQLite FTS5 search, `email_search`, or optional search filters.

Sources to re-check when in doubt:

- Project spec: `spec.md`, "Query Construction"
- Upstream README: https://github.com/bawkee/SqlBinder/blob/master/README.md
- Upstream source: https://github.com/bawkee/SqlBinder/tree/master/Source/SqlBinder

## Purpose

Use explicit SQL with parameters. Do not expose SQL to MCP clients.

SqlBinder is not an ORM. Treat it as a SQL-centric template processor that keeps large SQL readable while pruning optional criteria and producing bind parameters. It works well beside direct ADO.NET, Dapper-style execution, or other data-access code.

Use SqlBinder for optional normal SQL criteria. Do not use it as a parser/escaper for SQLite FTS query syntax, user-selected SQL fragments, file paths, or raw MCP input.

## Fit For LCE MCP

`email_search` is the main use case because filters can include:

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

Keep FTS handling separate:

1. Parse MCP arguments.
2. Convert account names/emails to local account IDs.
3. Parse user search text into a safe FTS expression.
4. Bind optional normal SQL criteria through SqlBinder.
5. Bind the safe FTS expression as a normal command parameter.
6. Execute message and/or attachment branches.
7. Merge, rank, and snippet results in app code.

## Core API

Typical use:

```csharp
var query = new Query(sqlTemplate);

query.SetCondition("accountIds", accountIds);
query.SetCondition("fromEmail", fromEmail, ignoreIfNull: true);
query.SetConditionRange("dateSent", fromDate, toDate);
query.SetCondition("hasAttachments", hasAttachments, ignoreIfNull: true);

var sql = query.GetSql();
var parameters = query.SqlParameters;
```

Useful overloads:

- `SetCondition(name, value)` adds equality for scalar values.
- `SetCondition(name, values)` adds `IN (...)` for multi-value collections; a single-item collection reduces to equality.
- `SetCondition(name, value, NumericOperator...)` supports numeric/date comparisons.
- `SetCondition(name, value, StringOperator...)` supports exact match, caller-supplied `LIKE`, contains, begins-with, and ends-with.
- `SetConditionRange(name, from, to)` emits `BETWEEN` when both bounds exist, `>=` or `<=` when only one bound exists.
- `isNot: true` flips supported operators to `<>`, `NOT IN`, `NOT BETWEEN`, or `NOT LIKE`.
- `ignoreIfNull: true` omits a nullable scalar condition; without it, null scalar equality becomes `IS NULL`.

`GetSql()` populates `SqlParameters`. If a condition name has no matching placeholder in the template, SqlBinder throws an unmatched-condition error. This is useful; let it catch misspelled template names.

## Template Syntax

Use braces for optional scopes:

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
    {m.date_sent :dateSent}
    {m.has_attachments :hasAttachments}}
ORDER BY m.date_sent DESC
```

Important rules:

- `{...}` is an optional scope.
- A scope with no matching active condition is removed with its parent scope if needed.
- Sibling scopes are automatically connected with `AND`.
- Nested scopes are valid and are the right way to make whole subqueries optional.
- A scope should contain either child scopes or one SqlBinder parameter placeholder.
- Put each placeholder in its own scope when a logical fragment needs multiple optional values.
- Default placeholders use `:name`, `@name`, or `?name`; SqlBinder keeps that prefix when generating SQL parameter placeholders.

Do not write `IN` yourself for the ordinary collection case. This:

```sql
{m.account_id :accountIds}
```

can become equality for one account or `IN (...)` for many accounts. Writing `m.account_id IN :accountIds` would duplicate the operator for collection conditions.

## Optional Subqueries

Wrap the whole optional subquery in a parent scope, then put each trigger condition in a child scope:

```sql
SELECT
    m.id,
    m.subject,
    m.date_sent
FROM messages m
{WHERE
    {m.account_id :accountIds}
    {m.id IN (
        SELECT r.message_id
        FROM message_recipients r
        {WHERE
            {r.email :recipientEmail}
            {r.name :recipientName}}
    )}}
ORDER BY m.date_sent DESC
```

If neither recipient condition is active, the `m.id IN (...)` scope disappears. If account IDs are also absent, the entire `WHERE` block disappears.

## OR And Manual Connectors

Prefix a scope with `@` to connect its child scopes with `OR` instead of `AND`:

```sql
{WHERE
    {m.account_id :accountIds}
    {m.id IN (
        SELECT r.message_id
        FROM message_recipients r
        WHERE
        @{ {r.email :participantEmail} {r.name :participantName} }
    )}}
```

Prefix a child scope with `+` when you need to preserve your own connector or whitespace instead of having SqlBinder insert `AND`/`OR`. Use this sparingly; normal sibling scopes are clearer.

## SQLite Parameters And FTS

Sharp edge: by default SqlBinder treats `:name`, `@name`, and `?name` as SqlBinder placeholders, not ordinary DB parameters. If a query also needs raw SQLite parameters such as `@fts` or `@limit`, choose one of these patterns:

1. Use SqlBinder only for the optional criteria fragment, then compose that fragment into a static command that owns ordinary DB parameters.
2. Prefer custom SqlBinder placeholder syntax for mixed templates, so raw SQLite parameters survive untouched.

Mixed-template pattern:

```csharp
using SqlBinder;
using SqlBinder.Parsing;

var query = new DbQuery(connection, sqlTemplate)
{
    ParserHints = ParserHints.UseCustomSyntaxForParams
};

query.FormatParameterName += (_, e) =>
{
    var parameterName = "@" + e.FormattedName;
    e.FormattedName = parameterName;
    e.FormattedForSqlPlaceholder = parameterName;
};

query.SetCondition("accountIds", accountIds);
query.SetConditionRange("dateSent", fromDate, toDate);

using var command = query.CreateCommand();
AddParameter(command, "@fts", safeFtsExpression);
AddParameter(command, "@limit", limit);
```

Template:

```sql
SELECT
    m.id,
    m.subject,
    bm25(messages_fts) AS rank
FROM messages_fts
JOIN messages m ON m.id = messages_fts.rowid
WHERE messages_fts MATCH @fts
{AND
    {m.account_id [accountIds]}
    {m.date_sent [dateSent]}}
ORDER BY rank, m.date_sent DESC
LIMIT @limit
```

With `ParserHints.UseCustomSyntaxForParams`, SqlBinder condition placeholders are `[accountIds]` / `[dateSent]`, while `@fts` and `@limit` remain normal SQLite parameters. Because bracket placeholders do not carry a parameter prefix, set `FormatParameterName` so generated SqlBinder placeholders and command parameter names use `@...`.

If custom syntax is enabled and literal square brackets are needed in SQL, escape them as doubled brackets.

## Execution Options

`Query` gives you SQL text plus `SqlParameters`. Use it when the surrounding storage code already owns command creation.

`DbQuery` can create an `IDbCommand` and populate parameters during `CreateCommand()`. Add any non-SqlBinder parameters, such as `@fts` and `@limit`, after `CreateCommand()` unless the storage helper does that for you.

Prefer a fresh `Query` / `DbQuery` instance per execution. SqlBinder has parser caching for template text, so caching the SQL template string is enough for normal hot paths. If an instance is ever reused deliberately, clear `Conditions` and `SqlParameters` before applying the next request.

## Variables And Raw Fragments

`DefineVariable` inserts text into the output SQL. Do not feed it user input, MCP arguments, raw sort fields, raw table names, raw filesystem paths, or raw FTS syntax.

If sort order or a projection differs by user option, map the user option to a small whitelist of static templates or static fragments in app code.

## FTS Boundary

SqlBinder solves optional SQL criteria composition. It does not make raw SQLite FTS syntax safe.

For FTS:

- Parse and escape the FTS expression separately.
- Bind the final safe expression as a normal SQLite parameter.
- Keep FTS `MATCH` clauses explicit in SQL.
- Never concatenate raw search text into a template, variable, or SQL string.

## Common Review Checks

- Are all MCP inputs parsed into typed values before they reach SqlBinder?
- Are collection filters using `{column :name}` rather than `column IN :name`?
- Are raw SQLite parameters protected from SqlBinder parsing when default placeholder syntax is used?
- Are nulls intentionally omitted with `ignoreIfNull: true`, or intentionally emitted as `IS NULL`?
- Are FTS expressions handled outside SqlBinder?
- Are sort fields, table names, projection names, and cursor SQL fragments whitelisted rather than user-supplied?
