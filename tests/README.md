# Regression tests

Run from the backend directory:

```bash
dotnet test
```

The tests cover cart migration discovery and SQL ordering, current-role authorization,
calendar privacy, and atomic refresh-token consumption and rollback. They do not use
production configuration, contact Telegram, or open a PostgreSQL connection.

Refresh tests use an isolated SQLite database in memory. Its test-only context skips
the PostgreSQL-specific computed login column. A command interceptor exercises the
case where a token changes between lookup and consumption.

The migration tests generate PostgreSQL SQL; they do not execute the migration chain.
Before deployment, run that chain against a disposable PostgreSQL database and inspect
the schema of any installation that created cart tables manually.

On 30 September 2026, an isolated PostgreSQL 18.3 smoke check also passed: the full
10-migration chain via EF, adoption of existing cart tables and the editing column
with missing migration history without losing seeded cart data, the Npgsql calendar
projection, two competing refresh requests with exactly one successful rotation,
and rollback after failed JWT signing. No production database was used for this check.

The calendar API now returns `CalendarBookingPayload`, so deploy its matching frontend
with the backend. It exposes the display name, optional Telegram username, reason,
period, status and equipment summaries; it has no User/Booking entity navigation.
