# Backend migrations (EF Core)

The server applies schema through versioned migrations at startup
(`MigrateAsync` in `backends/dotnet/Program.cs`). `EnsureCreated` is gone —
never bring it back: it cannot evolve a database that already holds data.

## Everyday commands (run from `backends/dotnet/`)

- New migration after a model change: `dotnet ef migrations add <Name>`
- Inspect pending state: `dotnet ef migrations list`
- SQL review without applying: `dotnet ef migrations script`
- Apply out of band: `dotnet ef database update`

## Baslining a pre-migrations database

Databases created by the old `EnsureCreated` path (dev DBs up to Sep 2026)
already contain the full V1 schema but have no migrations history, so a plain
`MigrateAsync` would try to re-create tables. Mark the baseline as applied
once per such database instead of resetting it:

```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('<InitialCreate timestamp>_InitialCreate', '10.0.11');
```

Use the exact `MigrationId` from `Migrations/*_InitialCreate.cs`
(`dotnet ef migrations list` shows it). Future migrations then apply
incrementally. Verify with `SELECT * FROM "__EFMigrationsHistory";`

## Environments

- CI / throwaway DBs: start empty, `MigrateAsync` builds everything. Nothing to do.
- Dev (`duelmasters` volume): baselined once (see above); data (users, decks) preserved.
- Production (when it exists): `MigrateAsync` at startup; review each migration
  with `dotnet ef migrations script` before deploying.
