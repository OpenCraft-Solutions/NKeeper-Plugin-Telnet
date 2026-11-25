# NKeeper Telnet plugin

The official `telnet` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: Telnet terminals in the browser. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9).

## What it does

Ported from NKeeper 1.x's Telnet terminals.

- **Service type** `telnet` ("Telnet (unencrypted)"), port 23 by default, checked by core's TCP health probe.
- **Session type** `telnet` ("Telnet"), on `telnet` services, through jump hosts too. Core opens the connection and speaks Telnet (terminal type `xterm-256color`, window size). With a credential, core answers the server's first login and password prompts, within 30 seconds of connecting. The plugin never sees the password: it carries the terminal's bytes both ways (NKeeper's `docs/architecture.md` §7.22 and §7.23).
  - `options`: the terminal's `columns` and `rows`. Anything that isn't a number in range falls back to 80 × 24.
  - Binary frames are the terminal's bytes; a text frame `resize` `{ columns, rows }` resizes it.
- **Host actions** `telnet.terminal` ("Telnet"), with a credential core signs in with, and `telnet.typed` ("Telnet (sign in yourself)"), without one, for the user to type at the prompts.
- **Frontend**: the terminal tab, built on xterm.js, fitted to its tab. When the connection closes it says so and offers to reconnect.

**Telnet isn't encrypted.** Everything between NKeeper and the host crosses the network in the clear, including the password core types. Prefer SSH wherever the device offers it.

The plugin's ID is `telnet`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper.

**Recordings** (1.1.0, SDK 1.16.1): its terminal is declared a recorded one, so core records what it showed, never what was typed, for the audit log (NKeeper's `docs/architecture.md` §7.51).

## Layout

```text
backend/        the plugin: TelnetPlugin.cs, nkeeper-plugin.json (the manifest), the project
frontend/       the terminal view: a Vite library build to dist/index.js
tests/          the backend tests, which start NKeeper's test server with this plugin loaded
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `NKeeper.Testing` | GitHub Packages (NuGet) | the test server, the PostgreSQL fixture and a fake Telnet device |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`) and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet restore NKeeper.Plugins.Telnet.slnx
dotnet build NKeeper.Plugins.Telnet.slnx --configuration Release
```

Building the plugin builds its frontend, packs `telnet-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load the package in a development NKeeper, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.Telnet.slnx --configuration Release
```

The tests need Docker for PostgreSQL. They start NKeeper's test server (from `NKeeper.Testing`) with this plugin's built package loaded and a development key trusted, and a fake Telnet device (`FakeTelnetServer`). They check what the plugin contributes, that core signs in and the terminal resizes and ends when the device closes, and that a user can sign in by typing.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints and type-checks the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v1.1.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `telnet-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
