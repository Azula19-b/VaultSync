# Contributing

Thank you for improving VaultSync. Open an issue before a large behavioral change. Security reports belong in a private GitHub security advisory.

## Development setup

1. Install the .NET 10 SDK.
2. Run `dotnet restore VaultSync.slnx`.
3. Run `dotnet build VaultSync.slnx`.
4. Run `dotnet test VaultSync.slnx`.

Use C# 14, nullable reference types, async APIs, and cancellation for file or database work. Preserve clean-architecture project boundaries. Never add sample passwords, keys, real vault data, personal documents, SQLite databases, or build output.

Tests must use unique directories under the operating system's temporary directory and clean up afterward. Any backup-format or cryptographic change needs compatibility documentation, corruption tests, and a safe migration story. Pull requests should be focused, explain data-safety implications, and leave `dotnet build` and `dotnet test` green.

