# User-managed toolchains, and Python

## The defect

A scratch repository declared `.mainguard/verify` = `python -m pytest -q`. Verification ran in the jail
(PR #307 wired the trigger) and reported **tests failed**. The tests had not failed; they had never run.
The agent's own diagnosis was correct:

> pytest isn't available in this environment — no `pip` module and apt filesystem is read-only. The
> Python installation in `/opt/toolchain/` doesn't have pytest and can't be updated.

Measured in a real container to confirm it rather than believe it:

| measurement | result |
| --- | --- |
| `/opt/toolchain/bin/python3 --version` in `mainguard-agent-base:latest` | works |
| `python3 -c "import pip"` in the same container | **fails** |
| `apt-get install` in the same container | fails — the rootfs is read-only |

So the base image has an interpreter and **no package manager**. A Python, Node or Go repository could
commit tests that could never run, and the merge queue recorded that as failing code. That is the worst
possible shape for a verification gate: a wrong verdict that looks like a real one.

`ToolchainCatalog` had exactly one entry, `dotnet-10`.

## Why the closed catalog was not the problem

`docs/review/phase-1-review-guide.md` §6.3 records the rule that must survive:

> The toolchain file selects from a closed catalog rather than describing an installation — a repo can
> say "this is a .NET project", not *how* .NET is obtained. That is what keeps a repo-writable file from
> being an install-time code-execution surface.

Two different actors were being conflated, and separating them is the whole design of this change:

- **A HUMAN choosing which toolchains exist in their environment** — fine, and now supported. It is
  their machine.
- **A REPOSITORY choosing what runs at install time** — still forbidden. `.mainguard/toolchain` still
  names a toolchain by id and can never supply a command, a URL, a version or a package list.

Making the *installed set* a user decision does not make the *contents* of a toolchain a repository
decision. Every entry stays pinned, checksum-verified product source.

## The two delivery kinds

One catalog (`ToolchainCatalog.All`) answers "may a repository declare this id?". Behind it there are now
two ways a toolchain reaches a jail, and which one applies is a **property of the artefact, not a
preference**:

### `ImageLayer` — unchanged

`dotnet-10`, `rust-stable`, `jdk-21`, `ruby-3`, `php-8`. Built into a per-repo image layer on the spawn
path by `ToolchainProvisioner`, exactly as before.

**Why these cannot become user-installed mounts.** `dotnet-10` needs `libicu72` and `libfontconfig1`
from apt — without ICU, .NET's globalization initialiser calls `Environment.FailFast` and every `dotnet`
invocation dies at SIGABRT; without fontconfig every headless Avalonia render test dies in a type
initialiser. A bind mount cannot deliver a system library into a read-only rootfs. The nix-sourced
recipes are the same story against the image's baked nix store. This is the honest answer to "why not one
mechanism": .NET is the outlier, and it is the outlier for a measurable reason.

### `RuntimeMount` — new

`python-3`, and any future toolchain that ships as a **self-contained relocatable tarball**. Installed
into the VM by `ToolchainChannel` when a human asks, then bind-mounted **read-only** into every jail at
`/opt/mainguard/toolchains`. No build, no root, no image rebuild — so G-16 is respected (nothing under
`images/` is touched by this change at all, and no image version constant moves).

This is deliberately the mechanism `AdapterChannel` already proved for agent CLIs, reused down to the
`IAdapterInstallHost` seam so there is one way to run a command in MainguardEnv rather than two.

**Read-only is load-bearing.** One toolchain tree is shared by every agent on the machine. If a jail
could write it, agent A could replace the interpreter agent B's *verification* runs under — the merge
gate decided by another tenant, which is the reasoning `PackageCachePolicy` records for keeping package
caches per-agent. Toolchains may be shared precisely because nothing in a jail can write them.

## The three questions the brief asked

### 1. How project dependencies are installed and cached

**The daemon-owned package cache was already there, and it already had a pip knob.**
`PackageCachePolicy.Environment` has set `PIP_CACHE_DIR={root}/pip` since MG-43. Nothing was added to it.

What was missing is that a *download* cache does not help if there is nowhere to **install** to: the
toolchain mount is read-only and `$HOME` is a 256 MiB tmpfs. So the `python-3` entry contributes:

```
PYTHONUSERBASE = /var/cache/mainguard/python/userbase
PIP_USER       = 1
```

`pip install` therefore lands in the per-agent package cache — an ext4 tree **outside** `/workspace` (so
`git add -A` cannot commit a dependency closure into the branch under verification) and **outside**
`$HOME` (which a real closure overruns). This is the same treatment `NUGET_PACKAGES` already gets, and
`{cache}/python/userbase/bin` is added to `PATH` so console scripts (`pytest`, `ruff`, `mypy`) are
runnable by name after a project installs them.

`PYTHONDONTWRITEBYTECODE=1` keeps `__pycache__` out of the worktree, for the same "nothing untracked in
the tree under verification" reason.

**Known caveat, stated rather than papered over:** `PIP_USER=1` is refused inside a virtualenv, so a
repository that builds its own venv must `unset PIP_USER` in its verify command. The alternative — not
setting it — means every Python repo needs a Mainguard-specific `--user` flag, which is worse.

### 2. Whether pytest is preinstalled

**It is not.** The toolchain supplies the *language and its package manager*; the repository supplies its
*own test framework*. That is one rule for both delivery kinds: `dotnet-10` ships the SDK and NuGet, not
xunit, and repositories declare xunit in their `.csproj`.

The decisive argument against preinstalling is not fairness between test runners, it is correctness. A
preinstalled pytest pins a version globally. A repository with `pytest==7.4` in `requirements.txt` would
then have an ambiguous `python -m pytest` — ours or theirs — and a verification jail that quietly
disagrees with the developer's machine about which test runner ran is the same class of defect the
`dotnet-10` recipe already refuses when it declines `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

The cost is one line in the repository's verify command, which is the ecosystem-normal shape:

```
pip install -q -r requirements.txt && python -m pytest -q
```

`pip` exists, PyPI is allowlisted, and the cache is real — so that line is cheap on every run after the
first. This is what the owner's scratch repo needs to change to.

### 3. Egress — verified, not assumed

`EgressAllowlist.DefaultEntries` already carries `pypi.org` and `files.pythonhosted.org` as
`PackageRegistry` entries, which take the direct CONNECT route rather than the P2-08 model gateway.
**Zero allowlist entries are added by this change.**

What was measured, and what was not:

- **Measured:** in a hardened container (read-only rootfs, `--cap-drop ALL`,
  `--security-opt no-new-privileges`, uid 1000, tmpfs `$HOME`, toolchain mounted read-only),
  `pip install -r requirements.txt` reached PyPI and succeeded, and `python -m pytest -q` then ran. This
  run used the default bridge, **not** the tinyproxy segment.
- **Not measured on this machine:** pip through the live `mainguard-egress-proxy`. Standing up a proxy
  segment by hand was abandoned after a `DockerSuiteFixture` sweep severed the owner's live jail from its
  network mid-session (see "What went wrong" below). `PythonToolchainDockerTests` routes through the real
  proxy segment and measures this in CI.

The toolchain **payload** host (`github.com` / the release-asset CDN) stays off the jail allowlist: it is
reached by the daemon on the VM's own network at install time, which is where
`images/mainguard-agent-base/Dockerfile` already reaches its own pinned artefacts.

## The `python-3` entry

python-build-standalone (astral-sh) — the distribution `uv` installs.

| property | value |
| --- | --- |
| version | 3.12.13 (release tag `20260623`) |
| payload | version-addressed HTTPS GitHub release asset |
| sha256 | `9fa869d6…48930`, taken from upstream's `SHA256SUMS` asset |
| includes pip | yes — 26.1.2 |
| relocatable | yes — `sys.prefix` is derived from the executable's path at runtime |

Two things about the pin are worth stating. The checksum is **upstream's published one**, cross-checked
against an independent download — a hash computed only from your own bytes answers "are these the bytes I
downloaded?", which is circular. And relocatability is what makes a read-only bind mount possible at all;
it was verified by extracting to an arbitrary directory, mounting it read-only into the hardened jail, and
confirming the interpreter reports its own mount point as `sys.prefix`.

**Why not nixpkgs**, which the base image already pins: nixpkgs' `python3` is the very interpreter that
has no pip, and `nix profile install` cannot express `python3.withPackages (ps: [ps.pip])` as a flake
attribute path.

### The probe

Deliberately not `python3 --version`:

```
{toolchain}/bin/python3 -c "import pip, sys; print(sys.version.split()[0] + ' pip ' + pip.__version__)"
```

Two reasons, both measured. The base image already carries a `python3` on `PATH`, so a bare version probe
can pass while this toolchain is entirely absent. And the defect is a *missing pip*, not a missing
interpreter — a probe that does not import pip would have reported the owner's broken environment as
healthy. It runs the toolchain's own interpreter by absolute path.

### An install is only an install once it RUNS

PR #305's lesson: an adapter's marker reported healthy for eleven days because a file had arrived while
the binary it pointed at was a stub. So:

- the install marker is written **last**, only after a probe that executes the toolchain and matches the
  pinned version in its output;
- `ToolchainChannel.ListAsync` **re-probes** rather than reading markers, so a settings page can never
  show "Installed" for something that does not work;
- a probe that exits 0 but reports the wrong version is a typed `VersionMismatch`, not a pass.

## Honest failure when a declared toolchain is missing

`SandboxAgentLauncher.EnsureMountedToolchainsAsync` runs **before the container is created** and refuses:

> this repository declares Python 3 (python-3) — Not installed, which is not installed in this Mainguard
> environment. Install it in Settings → Toolchains and start the agent again. The jail was NOT started: a
> jail without the toolchain cannot run this repository's tests, and a verification that fails for that
> reason would look like failing code.

Note what is **not** there: an auto-install. A repository's declaration is not permission to install
software. Installing on a repo's say-so would hand a repo-writable file exactly the install-time execution
the closed catalog exists to deny it.

## Evidence

End to end, in a hardened container, same toolchain and same command both ways — only the repository's
test changed:

```
INTERPRETER=/opt/mainguard/toolchains/python-3/bin/python3
.                                                                        [100%]
1 passed in 0.07s
EXIT=0
```

```
INTERPRETER=/opt/mainguard/toolchains/python-3/bin/python3
F                                                                        [100%]
>       assert 1 + 1 == 3
E       assert (1 + 1) == 3
FAILED tests/test_math.py::test_adds - assert (1 + 1) == 3
1 failed in 0.05s
EXIT=1
```

`INTERPRETER=` is not decoration: it proves the declared toolchain won the `PATH` race against the base
image's pip-less `python3`. Losing that race is a silent wrong-interpreter run, so
`ToolchainChannelTests` pins the ordering too — appending the toolchain entries instead of prepending
them makes that test fail with the base image's path in the message.

## Node and Go

Both are self-contained relocatable tarballs with upstream-published checksums, so both are
`RuntimeMount` toolchains of exactly the same shape as Python. **Adding them is an edit to
`toolchains.starter.json` and nothing else** — no C# change, no new delivery kind. They are out of scope
here deliberately: doing Python properly and proving it beats half-doing three.

## Deferred, with the design recorded

### Repo-declaration editing from the app (layer 2)

The owner's instruction: *"it should have the full flow built into the app, but have the user push all the
buttons so that they 'manually' perform every action."* Not implemented in this PR. The design it must
follow, so the next change does not have to re-derive it:

- `.mainguard/toolchain` **stays a committed file**, read out of the daemon-side bare mirror by
  `RepoToolchainConfig`. Only `main`'s declaration is ever provisioned and no jail can write it. No
  side-channel that bypasses the file.
- Discrete buttons, no compound actions: **Write file** → **Stage & commit** → **Push** → **Install
  toolchain** → build/verify. Each shows what it will do before doing it. Nothing happens as a side
  effect of anything else.
- Each button **disabled with a stated reason** when its precondition fails — dirty working tree, not on
  the default branch, nothing to commit. Disabled without a reason is a known-bad pattern here (PR #302).
- Resolve the default branch **dynamically** (`AgentRefMediator` already does). The owner's scratch repo
  is `master`. Never hardcode either name.
- Never stash, never check out on the user's behalf, never push as part of committing.

### Two pre-existing issues this change does not fix

Flagged rather than touched, because they live in the image-layer spawn path that other agents are
working in:

1. **`ToolchainProvisioner._buildGate` is an instance field on a per-spawn object.** Its comment claims
   two agents cannot race two identical builds; across spawns that is false, because
   `SandboxAgentLauncher.EnsureToolchainAsync` constructs a new provisioner every time.
2. **`DaemonBackedOrchestrator.SpawnDeadline` is 5 minutes** and a ~2.9 GB `dotnet-10` build can exceed
   it.

This change does not make either worse — a `RuntimeMount` toolchain builds nothing, so a Python
repository never enters the build path at all.

## A diagnostic trap worth writing down

While building this I ran one `RequiresDocker` test, saw `DockerSuiteFixture.SweepAsync` delete every
`mainguard-*` network, then found a running `mainguard-…` container with
`NetworkSettings.Networks == {}` — and reported that a live agent jail had been severed. **That was
wrong**, and three other agents reached the same wrong conclusion independently.

There are **two Docker daemons** on this machine:

| daemon | reached by | holds |
| --- | --- | --- |
| Docker Desktop | `docker ps` from the Ubuntu distro; `dotnet test` | test artifacts |
| MainguardEnv (20.10.24) | `wsl -d MainguardEnv -u root -- docker ps` | the **real** jails |

`mainguardd` runs *inside* MainguardEnv and talks to that distro's own socket, so a production jail is
**never** in Docker Desktop. The container I inspected was a test artifact, and the networks the sweep
deleted were created by the tests themselves. Verified afterwards against the right daemon: the real jail
was up and attached (`10.203.4.3`) on its own `mainguard-agent-…` segment, with the egress proxy and all
segments intact, throughout.

**Both engines hold containers named `mainguard-…`, so a `docker ps` that does not name its daemon is not
evidence.** Any claim about a jail's health has to say which daemon it came from.
