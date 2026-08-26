<div align="center">

![AniSync Next](docs/banner.svg)

**Deterministic, reviewable Shoko watch-state synchronization for AniList and MyAnimeList.**

</div>

AniSync Next is a clean-slate Shoko Server 6 Dev plugin. Shoko is always the source of truth: the plugin reads current Shoko episode state at execution time, plans an exact provider change, and either applies a safe forward change or leaves it for review.

AniSync Next is intentionally separate from legacy AniSync. It has a new package ID, route, configuration, OAuth connections, state, and history. Disable legacy AniSync before enabling AniSync Next so both plugins do not process the same watch event.

## Current scope

- Outbound Shoko → AniList and MyAnimeList synchronization.
- Fresh progress calculated from the highest currently watched normal episode using `LastPlayedAt`, including correct unwatch/decrease previews.
- Safe forward progress, completion status, and canonical 0–100 rating synchronization.
- Review-only decreases, unresolved mappings, stale previews, and permanent failures.
- anime-offline-database ID resolution with persisted trusted mappings; missing IDs require an explicit manual match.
- Per-user settings and provider connections, with API client credentials restricted to Shoko administrators.
- Persistent pending work, mappings, reviews, and grouped history in an atomic versioned JSON state file.
- Trigger coalescing, retryable work recovery, cancellation, and clean shutdown draining.

The first release does not write start/finish dates, infer rewatches, update repeat counts, import provider state into Shoko, run periodic review refreshes, or auto-select fuzzy title matches.

## Requirements

- A compatible Shoko Server 6 Dev build. AniSync Next is Dev-only and currently pins `Shoko.Abstractions` `6.0.0-alpha.81`.
- A MyAnimeList API application and/or AniList developer client.
- Legacy AniSync disabled while AniSync Next is active.

## Install through Shoko

Add this repository URL in Shoko's native plugin package manager:

```text
https://raw.githubusercontent.com/JosephZurita/AniSync-Next/manifest/manifest.json
```

Sync the repository, select **AniSync Next**, install a compatible **Dev** release, and restart Shoko when prompted. The plugin UI is available from Shoko's Plugins section and at `/anisync-next`.

Native upgrades replace the versioned plugin package but preserve AniSync Next's configuration, OAuth/provider data, mappings, review queue, retryable work, and history. To hold a build, pin it in Shoko's plugin manager. To roll back, choose one of the retained earlier Dev releases and enable it; unpin or select the latest release to resume upgrades. The manifest retains the newest 30 builds.

There is no migration from legacy AniSync. Enter API client credentials again as a Shoko administrator, then reconnect each user's provider accounts.

## Manual DLL fallback

Manual installation remains supported. Download `AniSync.Next.dll` and optionally `AniSync.Next.dll.sha256` from the latest [development release](https://github.com/JosephZurita/AniSync-Next/releases), place the DLL in Shoko's `plugins` directory, and restart Shoko. Use either the package-manager install or a manual DLL, never both.

## Configure

1. Create provider apps. Use `https://<your-shoko-host>/anisync-next/oauth/callback` as the callback URL.
2. Open **AniSync Next → Settings** as a Shoko administrator and enter the client IDs/secrets.
3. Each Shoko user connects their own AniList and/or MyAnimeList account.
4. Use **Review → Refresh from Shoko** to calculate the current differences. There is no periodic refresh.

Only four settings are exposed because each has a defined runtime effect: automatic sync, progress only on completion, rating sync, and adult-title inclusion for manual mapping search.

## Architecture

The distributable is one .NET 10 assembly, `AniSync.Next.dll`, with internal boundaries for:

- `Domain`: provider-neutral state, planning, changes, reviews, outcomes, and contracts.
- `Application`: fresh-state coordination, mapping resolution, execution, and structured failures.
- `Providers`: private MAL/AniList DTOs, OAuth, named HTTP transport, retry and token refresh.
- `Persistence`: atomic versioned state and corruption backup.
- `Host`: Shoko state/event adapters and the tracked background worker.
- `Api`: authenticated typed endpoints and embedded React assets.

The generated package identity is locked as `8eea2528-a2f8-543a-8bc5-a06bb5a138bd` by assembly and manifest tests.

## Development

```bash
cd client
pnpm install --frozen-lockfile
pnpm lint
pnpm typecheck
pnpm test
pnpm build
cd ..

dotnet restore AniSync.Next.slnx
dotnet test AniSync.Next.slnx
```

The Vite build writes into `src/AniSync.Next/wwwroot/app`; the plugin project embeds those files into the DLL.

Domain/application coverage is enforced at 80% lines and 70% branches. Provider adapters use fake HTTP handlers for pagination, GraphQL errors, 401 refresh, `Retry-After`, timeouts, and cancellation. Release tests lock package identity, deterministic manifest history, ZIP contents, and SHA-256 integrity.

## Development releases

Every successful `master` build publishes version `0.1.0-dev.<GitHub run number>` on the Dev channel. GitHub Actions validates frontend lint/typecheck/tests/build, the full .NET suite, application coverage, plugin registration metadata, DLL identity/version, archive contents, checksums, and manifest history before publishing:

- `AniSync.Next.dll` and `AniSync.Next.dll.sha256` for manual installs.
- `AniSync.Next-0.1.0-dev.<run>.zip` and its checksum for Shoko.
- A newest-first, rerun-idempotent `manifest.json` on the dedicated `manifest` branch.

Dependabot tracks the exact Shoko plugin packages. Shoko abstraction upgrades must pass the full pipeline before a new package is published.
