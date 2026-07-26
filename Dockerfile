# =============================================================================
# Mainguard — DEVELOPMENT / BUILD / CI container
# =============================================================================
# This image reproduces a *consistent build & test toolchain* across every
# contributor's machine (Windows, macOS, Linux). It pins the .NET 10 SDK plus
# the native libraries LibGit2Sharp and SkiaSharp need, and can run the full
# headless test suite (Avalonia.Headless) with no display server.
#
# IMPORTANT: This container is for BUILD, TEST, and EF migrations — NOT for
# running the Mainguard desktop GUI for end users. Shipping a desktop app inside
# a container means X11/Wayland forwarding, which is fragile and does NOT
# "run the same everywhere." End-user distribution stays native per-OS
# (Velopack: .exe / .dmg / .AppImage), as already planned in the roadmap.
# See "How to use" at the bottom of this file.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dev

# --- Native runtime deps -----------------------------------------------------
# git            : CLI fallback engine used by GitService (ExecuteGitCli et al.)
# git-lfs        : real git-lfs binary so the RequiresGitLfs integration tests
#                  (GitServiceLfsTests) actually run instead of skipping red
# gnupg          : real gpg so the RequiresGpg signing tests (GitServiceSigningTests)
#                  can generate an ephemeral key and sign/verify end-to-end
# libgit2 deps   : libssl / libssh2 for LibGit2Sharp network transport
# skia/font deps : libfontconfig1, libice6, libsm6, libx11-6 so SkiaSharp
#                  (LiveCharts, AvaloniaEdit, headless render tests) loads
# libicu         : globalization (already in SDK image, kept explicit)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        git-lfs \
        gnupg \
        ca-certificates \
        libssl3 \
        libssh2-1 \
        libfontconfig1 \
        libice6 \
        libsm6 \
        libx11-6 \
        libicu-dev \
        xvfb \
    && rm -rf /var/lib/apt/lists/*

# Restore the local dotnet tools (dotnet-ef) declared in dotnet-tools.json.
ENV PATH="${PATH}:/root/.dotnet/tools"
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

WORKDIR /src

# --- Layer-cache the restore -------------------------------------------------
# Copy only the project graph first so `dotnet restore` is cached until a
# .csproj / solution actually changes. Each project's packages.lock.json rides along with its
# .csproj, and Directory.Build.props (which turns lockfiles on) with the solution — without them
# this layer would resolve an UNLOCKED graph and the container toolchain would stop reproducing
# what CI builds, which is the whole point of the wrapper (MG-35).
COPY global.json Mainguard.slnx dotnet-tools.json Directory.Build.props ./
COPY Mainguard.Git/Mainguard.Git.csproj                     Mainguard.Git/packages.lock.json                     Mainguard.Git/
COPY Mainguard.Agents/Mainguard.Agents.csproj               Mainguard.Agents/packages.lock.json                   Mainguard.Agents/
COPY Mainguard.UI/Mainguard.UI.csproj                       Mainguard.UI/packages.lock.json                       Mainguard.UI/
COPY Mainguard.Agents.UI/Mainguard.Agents.UI.csproj         Mainguard.Agents.UI/packages.lock.json                 Mainguard.Agents.UI/
COPY Mainguard.App.Shell/Mainguard.App.Shell.csproj         Mainguard.App.Shell/packages.lock.json                 Mainguard.App.Shell/
COPY Mainguard.Client.App/Mainguard.Client.App.csproj       Mainguard.Client.App/packages.lock.json               Mainguard.Client.App/
COPY Mainguard.Pro.App/Mainguard.Pro.App.csproj             Mainguard.Pro.App/packages.lock.json                   Mainguard.Pro.App/
COPY Mainguard.Protos/Mainguard.Protos.csproj               Mainguard.Protos/packages.lock.json                   Mainguard.Protos/
COPY Mainguard.Server/Mainguard.Server.csproj               Mainguard.Server/packages.lock.json                   Mainguard.Server/
COPY Mainguard.Server.Tests/Mainguard.Server.Tests.csproj   Mainguard.Server.Tests/packages.lock.json             Mainguard.Server.Tests/
COPY Mainguard.Tests/Mainguard.Tests.csproj                 Mainguard.Tests/packages.lock.json                     Mainguard.Tests/
# Mainguard.Tests ProjectReferences this nested harness, so restore needs its csproj too.
COPY Mainguard.Tests/TestTools/ScriptedAgent/ScriptedAgentHarness.csproj Mainguard.Tests/TestTools/ScriptedAgent/packages.lock.json Mainguard.Tests/TestTools/ScriptedAgent/
COPY installer/Mainguard.Installer/Mainguard.Installer.csproj                   installer/Mainguard.Installer/packages.lock.json           installer/Mainguard.Installer/
COPY installer/Mainguard.Installer.Elevated/Mainguard.Installer.Elevated.csproj installer/Mainguard.Installer.Elevated/packages.lock.json  installer/Mainguard.Installer.Elevated/
COPY installer/Mainguard.Uninstall/Mainguard.Uninstall.csproj                   installer/Mainguard.Uninstall/packages.lock.json           installer/Mainguard.Uninstall/
RUN dotnet tool restore && dotnet restore Mainguard.slnx --locked-mode

# --- Copy the rest & build ---------------------------------------------------
COPY . .
RUN dotnet build Mainguard.slnx -c Release --no-restore

# Default: run the test suites headlessly.
CMD ["dotnet", "test", "Mainguard.slnx", "-c", "Release", "--no-build"]
