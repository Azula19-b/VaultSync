# VaultSync security model

## Supported version

Security fixes are applied to the `main` branch. Please report suspected vulnerabilities privately through GitHub's security-advisory feature; do not include vaults, passwords, or personal files in an issue.

## Cryptographic design

- A new vault generates a 256-bit master encryption key with `RandomNumberGenerator`.
- The password is processed with PBKDF2-HMAC-SHA256 using a unique 256-bit salt and a default work factor of 600,000 iterations. The implementation rejects work factors below 210,000.
- The derived key-encryption key wraps the master key with AES-256-GCM. The vault UUID is authenticated as associated data.
- The password, derived key, and plaintext master key are never serialized. Sensitive key buffers are cleared when practical; the unlocked key exists in process memory while a vault is open.
- Every stored object and manifest uses authenticated, independently encrypted 1 MiB chunks. Each chunk receives a fresh random 96-bit nonce and a 128-bit tag. Associated data binds the object hash or manifest hash, chunk index, and plaintext length.
- SHA-256 provides content addressing, deduplication, and an independent post-decryption integrity check. It is not used as a password KDF.
- Encrypted manifests are authoritative recovery metadata. SQLite is a disposable local index and can be rebuilt only after every manifest passes AEAD and SHA-256 verification.

## Safety properties

VaultSync writes encrypted objects and manifests to unique temporary files, flushes them, and then atomically renames them. A snapshot becomes `Completed` in a SQLite transaction only after its encrypted manifest is durable. Existing snapshots are immutable. A cross-process exclusive lock prevents concurrent writers. Restore uses temporary files, validates tags and SHA-256, contains every path under the selected recovery root, and only then finalizes the file.

Symbolic links and reparse points are skipped. The vault and source may not contain one another. Source files are opened read-only and are never changed or deleted.

## Threat model and limitations

VaultSync protects vault contents at rest against someone who obtains the vault files without the password. It detects accidental or malicious ciphertext modification when content is read. It does not hide file counts, encrypted object sizes, vault creation time, or the unencrypted vault name/header parameters. Content hashes are visible in object filenames and may reveal equality within a vault.

VaultSync is not a defense against malware, keyloggers, memory inspection, or an attacker controlling the machine while the vault is unlocked. PBKDF2 is deliberately portable but is not memory-hard; Argon2id is a planned option. Deleted temporary plaintext is not securely erased from SSD media. No remote replication, hardware-backed key store, multi-user authorization, or code signing is provided in this release.

Backups run only while the desktop application is open. Keep at least one offline or physically separate copy and periodically test restores. VaultSync is engineering portfolio software, not a substitute for a professionally audited enterprise backup product.

