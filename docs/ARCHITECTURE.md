# Architecture

VaultSync separates policy from operating-system and storage details:

```text
VaultSync.Desktop       Avalonia views, MVVM commands, navigation, progress
        ↓
VaultSync.Application   backup/recovery orchestration, scheduling, contracts
        ↓
VaultSync.Core          manifests, domain records, path and safety rules
        ↑
VaultSync.Infrastructure  filesystem, AES-GCM, SQLite, vault object store
```

## Backup transaction

1. Validate that source and vault cannot recurse into one another and acquire the exclusive vault writer lock.
2. Create a `Pending` snapshot row.
3. Enumerate nested regular files while skipping symbolic links/reparse points and configured exclusions.
4. Hash a stable read of each file with SHA-256. If length or modification time changes during hashing, retry once and then fail safely.
5. Reuse an existing content-addressed object or stream the file into a temporary, chunk-authenticated AES-GCM object and atomically rename it.
6. Serialize the complete snapshot manifest, encrypt/authenticate it, and atomically publish it.
7. Commit snapshot files, object index rows, event history, and the `Completed` state in one SQLite transaction.

Failure before step 7 leaves every previous snapshot intact. A possible orphan encrypted object is harmless and can be reclaimed by a future maintenance tool.

## Automatic operation

`FileSystemWatcher` observes nested changes. Events only mark the source dirty; a three-second debounce coalesces noisy writes. The monitor triggers a full reconciliation scan after the configured interval even when no notification arrives, because filesystem notifications can be coalesced or lost. Monitoring is intentionally in-process and stops when VaultSync closes.

## Restore transaction

The selected snapshot maps logical relative paths to immutable objects. Each path is normalized and proven to remain beneath the recovery root. Content decrypts to a unique temporary file, every AEAD tag is validated, and SHA-256 must match the manifest before atomic finalization. Existing files require the explicit overwrite checkbox. Individual errors are returned as partial failures.

