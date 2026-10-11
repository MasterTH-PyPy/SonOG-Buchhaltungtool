# SonOG Buchhaltung – Hinweise für Änderungen

- **Versionsstand bei jedem Commit fortschreiben:** In `Directory.Build.props` `BuildNummer` um 1 erhöhen (im selben Commit).
  Bei größeren Änderungen `VersionPrefix` (z. B. 1.0 → 1.1) hochsetzen; die BuildNummer läuft weiter.
- **Vor Änderungen den aktuellen Stand holen** (`git fetch` / `git pull`): Rudolf arbeitet parallel im Repo weiter.
- Echte Buchhaltungsdaten (Kontoauszüge, Rechnungen, Taxpool-Dateien `*.dbb`/`*.dbd`) nie ins Repo; Tests nur mit erfundenen Daten.
- Build/Tests: `dotnet build SonOG.Buchhaltung.sln`, `dotnet test tests/SonOG.Buchhaltung.Tests` (CI: GitHub Actions auf Windows).
