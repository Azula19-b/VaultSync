# VaultSync

**Secure cross-platform backup and recovery application built with C#, .NET, Avalonia UI, AES-256-GCM encryption, incremental snapshots, and intelligent deduplication.**

VaultSync addresses a practical failure mode: important files disappear through accidental deletion, corruption, or device loss, while naive backups waste space by copying unchanged and duplicate content. It is a real desktop backup engine—not a mock dashboard. It scans actual directories, writes encrypted content-addressed objects, preserves immutable version manifests, and restores verified files.

> Status: portfolio release. The core backup, encryption, snapshot, rebuild, and recovery paths are implemented and covered by isolated automated tests. Review [Security and limitations](#security-and-limitations) before protecting important data.

## Features

- Manual incremental backup with live stage, file, byte, and percentage progress
- Automatic in-process monitoring with `FileSystemWatcher`, event debouncing, and periodic full reconciliation
- Nested folder preservation, exclusions, source/vault recursion prevention, symlink avoidance, cancellation, and an exclusive writer lock
- SHA-256 content addressing: unchanged and duplicate content reuses one encrypted object while every logical path remains visible
- AES-256-GCM authenticated streaming in 1 MiB chunks; large files are never loaded entirely into memory
- Random master key protected by password-derived PBKDF2-HMAC-SHA256 key encryption
- Immutable encrypted snapshot manifests plus a rebuildable SQLite index
- Historical snapshot/file browser, duplicate groups, real storage analytics, and event history
- Safe restore with traversal containment, no-overwrite default, temporary files, AEAD validation, and final SHA-256 verification
- Dark Avalonia 12 dashboard with Backup Manager, Recovery Center, Security Center, Analytics, and Settings
- Unit and end-to-end integration tests using disposable temporary filesystems

## Technology

C# 14 · .NET 10 · Avalonia UI 12 · CommunityToolkit.Mvvm · EF Core 10 · SQLite · `System.Security.Cryptography` · xUnit · GitHub Actions

## Architecture

```text
src/
├── VaultSync.Core/            Domain records, manifests, path safety
├── VaultSync.Application/     Backup/recovery workflows and contracts
├── VaultSync.Infrastructure/  AES-GCM, filesystem, SQLite, monitoring
└── VaultSync.Desktop/         Avalonia UI and MVVM view models
tests/
├── VaultSync.UnitTests/
└── VaultSync.IntegrationTests/
docs/                          Architecture and database design
```

See [Architecture](docs/ARCHITECTURE.md) and [Database schema](docs/DATABASE.md) for implementation-level detail.

## Prerequisites

- macOS 13+ on Apple Silicon or Intel, Windows 10+, or a modern desktop Linux distribution
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git; VS Code with C# Dev Kit is optional

## Install on macOS Apple Silicon

System-wide Homebrew installation:

```bash
brew install --cask dotnet-sdk
git clone https://github.com/Azula19-b/VaultSync.git
cd VaultSync
dotnet restore VaultSync.slnx
dotnet build VaultSync.slnx
```

If administrator access is unavailable, install the SDK inside the clone:

```bash
git clone https://github.com/Azula19-b/VaultSync.git
cd VaultSync
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$PWD/.dotnet"
./.dotnet/dotnet restore VaultSync.slnx
./.dotnet/dotnet build VaultSync.slnx
```

## Launch

```bash
dotnet run --project src/VaultSync.Desktop
```

For the project-local SDK, replace `dotnet` with `./.dotnet/dotnet`.

## First vault and backup

1. Open **Security Center** and choose an empty folder on a separate disk or outside the source tree.
2. Enter a vault name and a password of at least 10 characters, then select **Initialize new vault**. Losing this password means losing access; it is never stored.
3. Open **Backup Manager**, select the folder to protect, review exclusions, and select **Back up now**.
4. Wait for `Completed`. A second no-change backup creates a new version manifest but no redundant encrypted content objects.
5. To monitor changes, set the reconciliation interval under **Settings** and select **Enable / pause automatic backup**. Automatic operation lasts only while the app is running.

The source and vault cannot contain each other. VaultSync never changes or deletes source files.

## Recover a deleted file

1. Delete or modify a file in a disposable test source after a completed snapshot.
2. Open **Recovery Center**, select the earlier completed snapshot, and choose a recovery folder.
3. Leave overwrite disabled unless replacement is intentional.
4. Select **Restore and verify**. VaultSync authenticates encrypted chunks, checks SHA-256, and preserves the relative directory tree.

## Verified demo walkthrough

Create disposable content before launching:

```bash
mkdir -p /tmp/vaultsync-demo/source/nested /tmp/vaultsync-demo/vault /tmp/vaultsync-demo/restore
printf 'original content\n' > /tmp/vaultsync-demo/source/report.txt
cp /tmp/vaultsync-demo/source/report.txt /tmp/vaultsync-demo/source/report-copy.txt
printf 'nested note\n' > /tmp/vaultsync-demo/source/nested/note.txt
dotnet run --project src/VaultSync.Desktop
```

Initialize `/tmp/vaultsync-demo/vault`, protect `/tmp/vaultsync-demo/source`, and run snapshot 1. Then:

```bash
printf 'edited content\n' > /tmp/vaultsync-demo/source/report.txt
rm /tmp/vaultsync-demo/source/report-copy.txt
```

Run snapshot 2, select snapshot 1 in Recovery Center, restore to `/tmp/vaultsync-demo/restore`, and verify:

```bash
test "$(cat /tmp/vaultsync-demo/restore/report-copy.txt)" = "original content" && echo 'Verified'
```

## Tests

```bash
dotnet restore VaultSync.slnx
dotnet build VaultSync.slnx --no-restore
dotnet test VaultSync.slnx --no-build
```

The suite covers known SHA-256 vectors, path rules, password failure, scheduler bounds, traversal attempts, incremental no-change backups, duplicate objects, file modification/deletion, historical restore, authenticated corruption rejection, index rebuild, and interrupted-backup isolation. Tests only write beneath unique operating-system temporary directories.

## Security and limitations

The vault uses AES-256-GCM, unique random chunk nonces, authenticated context metadata, a random master key, and PBKDF2-HMAC-SHA256 password wrapping. See [SECURITY.md](SECURITY.md) for the threat model and design rationale.

Known limitations:

- Scheduling and watching stop when the app closes; there is no daemon/service yet.
- There is no cloud replication, retention pruning UI, OS notification integration, secure hardware key store, or package signing.
- PBKDF2 is portable but not memory-hard; Argon2id is a future option.
- Files that change between the stable hash pass and encryption are rejected by a second SHA-256 calculation performed during streaming encryption.
- This release skips symbolic links rather than following or recreating them.

## Screenshots

The application was launch-verified on macOS from the compiled `net10.0` output. This repository intentionally does not include a fabricated mock screenshot; the current automation host could not address a command-line-launched Avalonia process as a capturable application bundle.

## Troubleshooting

- **“No .NET SDK found”** — run `dotnet --info`; install .NET 10 or use the project-local command shown above.
- **Vault/source validation error** — choose two independent directory trees. Never place the vault inside the protected folder.
- **Wrong password or modified header** — both produce the same safe authentication failure by design.
- **Permission/locked-file failure** — close the application holding the file or grant the current user read access; the failed snapshot remains non-completed.
- **Lost `vault-index.db`** — unlock the vault and choose **Rebuild index from encrypted manifests** in Security Center.
- **Existing restore file** — choose another folder or explicitly enable overwrite.

## Future improvements

- Background service/launch agent, notification center integration, and durable schedules
- Argon2id and OS hardware-backed secret integration
- Retention policies, verified garbage collection, and snapshot comparison UI
- Sparse-file metadata, permissions, extended attributes, and opt-in symlink support
- Remote/offline replication, bandwidth controls, and signed installers
- Independent security audit and format-compatibility fixtures

## License

[MIT](LICENSE)
