# SQLite schema

The per-vault `vault-index.db` database is an operational index, not the sole recovery record. Authenticated manifests under `manifests/` can rebuild it from scratch.

| Table | Purpose | Important constraints/indexes |
| --- | --- | --- |
| `Vaults` | Vault identity metadata | unique external UUID |
| `ProtectedFolders` | Source and schedule configuration | unique vault/source pair |
| `BackupJobs` | Job lifecycle records | external UUID and timestamps |
| `BackupSnapshots` | Snapshot state and totals | indexed creation time |
| `SnapshotFiles` | Logical path → content hash | unique snapshot/path; hash index |
| `StoredObjects` | Deduplicated object catalog | unique SHA-256 hash |
| `BackupEvents` | User-visible activity | indexed timestamp |
| `ApplicationSettings` | Key/value preferences | unique key |

`SnapshotFiles` has a required relationship to `BackupSnapshots` with cascade deletion in the generated EF Core migration. SQLite foreign keys, primary keys, and unique indexes are created by `InitialCreate`. No password or encryption key is stored in the database.

