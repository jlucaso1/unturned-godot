# Cloud environments

How to get an agent sandbox — [Claude Code on the web](https://code.claude.com/docs/en/cloud-environments)
or [Codex](https://learn.chatgpt.com/docs/environments/cloud-environment) — to a state where
`dotnet build`, `dotnet test`, `dotnet format` and the coverage gate all just work, including the tests
that read real game data.

Both platforms boot an ephemeral Ubuntu container with the repo cloned, and neither ships the .NET SDK or,
of course, a copy of Unturned. One script covers both:

```sh
./scripts/setup-cloud-env.sh
```

It installs the .NET SDK 10, warms the NuGet cache, and downloads the game content, overlapping the
toolchain install with the download so the whole thing lands in about two and a half minutes on a cold
container. It is idempotent, so running it again on a warm one costs a few seconds.

Godot itself is not installed: the editor is only needed to *run* the game, and the test suite, the
coverage gate and `dotnet format` do not need it.

## Where the game content comes from

Unturned's **dedicated server** (Steam app `1110390`) is downloadable through Valve's anonymous account —
no login, no owned copy, no Steam client. It ships the same content the game client reads:
`Bundles/core_linux.masterbundle` (the same SerializedFile object graph, meshes and textures included),
the `Bundles/*.dat` asset definitions, and the official maps with their Landscape tiles.

`scripts/fetch-game-data.sh` pulls it with [DepotDownloader](https://github.com/SteamRE/DepotDownloader)
(pinned by version and SHA-256), filtered down to what the tests read: the bundles plus PEI, ~165 MB
instead of the server's full ~1.7 GB.

```sh
./scripts/fetch-game-data.sh                        # bundles + PEI, into build/game-data
./scripts/fetch-game-data.sh --maps PEI,Washington  # more maps
./scripts/fetch-game-data.sh --maps all             # every official map, ~1.5 GB
./scripts/fetch-game-data.sh --manifest-key         # cache key for the current depot manifests

export UNTURNED_PATH="$(./scripts/fetch-game-data.sh --print-dir)"
```

Everything lands in `build/`, which is git-ignored: no game content is ever committed, and each run is a
fresh fetch from Steam's own CDN. See [NOTICE.md](../NOTICE.md).

### Domains it needs

| Host | Why |
|---|---|
| `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` | the pinned DepotDownloader release |
| `api.steampowered.com` | Steam's connection-manager directory |
| `*.steamserver.net` | the connection manager itself |
| `*.steamcontent.com` | the content CDN the depots stream from |

The .NET SDK comes from Ubuntu's own archive (`archive.ubuntu.com`, `security.ubuntu.com`) and NuGet from
`api.nuget.org`; both platforms allow those by default.

### A note on proxied sandboxes

Both platforms route outbound traffic through an HTTP/HTTPS proxy. Steam hands out a randomized list of
connection managers, and only the candidates it offers on `:443` survive that proxy — the `:27019` ones
time out. `fetch-game-data.sh` retries the whole connection (`--retries`, default 8), which re-queries the
directory and draws a fresh candidate; in practice it connects within the first couple of attempts.

## Claude Code on the web

Environments live behind the cloud icon above the message box at
[claude.ai/code](https://claude.ai/code) → **Add cloud environment**.

**Network access.** Pick **Custom**, keep *Also include default list of common package managers* checked,
and add:

```text
api.steampowered.com
*.steamserver.net
*.steamcontent.com
```

**Setup script.** Runs as root before Claude starts, and its result is snapshotted, so later sessions in
the same environment skip it entirely. Keep it under ~5 minutes; this one measures about 2m30s cold.

```bash
#!/bin/bash
set -u

repo="$(find /home /workspace /root -maxdepth 5 -name unturned-godot.sln -printf '%h\n' 2>/dev/null | head -1)"
if [ -n "$repo" ]; then
  bash "$repo/scripts/setup-cloud-env.sh" || true
else
  # The clone is not in place yet; install the toolchain here and let the SessionStart hook below
  # fetch the game content on the first session.
  DEBIAN_FRONTEND=noninteractive apt-get update -qq && apt-get install -y -qq dotnet-sdk-10.0 || true
fi

exit 0
```

The `|| true` and the final `exit 0` matter: a non-zero exit fails the whole session, and a temporarily
unreachable Steam is not worth that — without content, the data-backed tests simply self-skip and the rest
of the suite still runs.

**SessionStart hook.** Already committed, so there is nothing to configure: `.claude/settings.json` runs
`.claude/hooks/session-start.sh` at the start of every cloud session. It re-runs the setup script (a no-op
once the snapshot has everything) and, importantly, exports `UNTURNED_PATH` through `$CLAUDE_ENV_FILE` so
every later Bash command in the session finds the content. It exits immediately outside the cloud, so local
sessions are untouched — your own machine already has its SDK and, most likely, a real Steam install that
the project finds by itself.

**No environment variables needed.** The hook sets `UNTURNED_PATH`. Set it in the environment dialog only
if you want the content somewhere other than `build/game-data`; the setup script and the hook both honour it.

## Codex

Environments live at [chatgpt.com/codex/settings/environments](https://chatgpt.com/codex/settings/environments).

Codex differs from Claude Code in two ways that shape the setup:

- The **setup phase has full internet, the agent phase does not** (it is off by default). Everything the
  task will need — SDK, NuGet packages, game content — has to be downloaded during setup.
- **Setup runs in a separate Bash session**, so `export` does not reach the agent. Environment variables
  have to be set in the environment's own settings.

**Setup script.** Fetch into a fixed absolute path rather than the repo, so the value you put in the
environment variables below never depends on where Codex mounts the clone:

```bash
#!/bin/bash
set -u

export UNTURNED_PATH=/opt/unturned-data
./scripts/setup-cloud-env.sh || true

exit 0
```

**Environment variables.** Add one, matching the path above:

```text
UNTURNED_PATH=/opt/unturned-data
```

**Maintenance script** (optional; runs when a cached container is resumed, within 12 hours). The same
script again — it is idempotent, and re-running it picks up any new NuGet dependency the branch added:

```bash
#!/bin/bash
set -u

export UNTURNED_PATH=/opt/unturned-data
./scripts/setup-cloud-env.sh || true

exit 0
```

**Agent internet access.** Leave it off if you like; the setup script has already fetched everything.
Turning it on (allowlist mode) with the [domains above](#domains-it-needs) lets the agent re-fetch content
or restore a newly added package mid-task.

**Container cache.** Codex keeps the container for up to 12 hours and invalidates it when you edit the
scripts, the environment variables or the secrets, which is exactly when the setup needs to run again.

## Skipping the download

Set `UNTURNED_SETUP_MAPS=none` and `setup-cloud-env.sh` installs the toolchain only. The suite stays green:
every test that touches real content self-skips, which is the same thing that happens on the hermetic CI
runners. Use it when the work is in `core/` logic that has no data dependency.

To pull more than PEI, set `UNTURNED_SETUP_MAPS=PEI,Washington` (or `all`). Only PEI is required — it is
the only map the test suite reads.

## GitHub Actions

Two workflows split along the same line:

- [`ci.yml`](../.github/workflows/ci.yml) is hermetic: no game data, on Linux, Windows and macOS. The
  data-backed tests self-skip, so it proves the pure-logic half on all three platforms.
- [`real-data.yml`](../.github/workflows/real-data.yml) fetches the content and runs the same suite plus
  the coverage gate against it. The content is cached on the depot manifest IDs, via
  `fetch-game-data.sh --manifest-key`, so a run re-downloads only when Valve ships an update. It also
  asserts the content really is on disk before testing, since a silently missing download would leave the
  job green while proving nothing.
