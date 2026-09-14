using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F63 — the five discrete launchd defects. Everything here reads the RENDERED plist, so it runs on any
/// platform and never touches launchd; what needs a real Mac (does launchd accept it) is the one thing a
/// unit test could not have proved anyway.
/// </summary>
public sealed class MacLaunchAgentPlistTests
{
    private const string Dotnet = "/usr/local/share/dotnet/dotnet";

    [Fact]
    public void Plist_IsWellFormedXml_AndEscapesEveryInterpolatedValue()
    {
        // The shape that broke it: a home directory or app name containing an ampersand. Ordinary on
        // macOS ("Bob & Alice's Mac"), and it produced a document launchctl bootstrap refuses.
        var dll = "/Users/a & b/Library/Application Support/Mainguard <dev>/Mainguard.Server.dll";
        var logs = "/Users/a & b/.mainguard/logs";

        var plist = MacDaemonLaunchAgent.RenderPlist(Dotnet, dll, Path.GetDirectoryName(dll)!, logs);

        // Parses at all — the pre-fix render did not, for this input.
        var document = XDocument.Parse(plist);

        // ...and round-trips to the ORIGINAL path, not to an escaped-looking approximation of it.
        var arguments = document.Descendants("array").First().Elements("string").Select(e => e.Value).ToArray();
        Assert.Equal(new[] { Dotnet, dll }, arguments);
    }

    [Fact]
    public void Plist_RunsAnAbsoluteDotnetMuxer()
    {
        var plist = MacDaemonLaunchAgent.RenderPlist(
            Dotnet, "/staged/Mainguard.Server.dll", "/staged", "/logs");
        var program = XDocument.Parse(plist).Descendants("array").First().Elements("string").First().Value;

        // F63a: launchd's PATH is /usr/bin:/bin:/usr/sbin:/sbin — no /usr/local/share/dotnet, no
        // Homebrew — so a bare "dotnet" here is a job that cannot exec.
        Assert.Equal(Dotnet, program);
        Assert.True(Path.IsPathRooted(program));
        Assert.NotEqual("dotnet", program);
    }

    [Fact]
    public async Task Install_RefusesToWriteAPlist_WhenNoAbsoluteMuxerIsKnown()
    {
        // The payload is missing, so InstallAsync must refuse without writing anything — the same
        // refusal path a missing muxer takes, and the one that matters: writing a job that cannot start
        // hides the real problem behind an infinite respawn.
        var empty = Path.Combine(Path.GetTempPath(), "mainguard-launchagent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        Assert.False(await new MacDaemonLaunchAgent().InstallAsync(empty));
    }

    [Fact]
    public void Plist_KeepAlive_IsConditional_AndThrottled()
    {
        var plist = MacDaemonLaunchAgent.RenderPlist(
            Dotnet, "/staged/Mainguard.Server.dll", "/staged", "/logs");
        var root = PlistDict(plist);

        // F63b: `<key>KeepAlive</key><true/>` respawns forever, including after a permanent failure —
        // and (pre-F55) every respawn rotated the live daemon's credentials on its way to failing.
        var keepAlive = ValueAfter(root, "KeepAlive");
        Assert.Equal("dict", keepAlive.Name.LocalName);
        Assert.Equal("true", ValueAfter(keepAlive, "Crashed").Name.LocalName);
        Assert.Equal("false", ValueAfter(keepAlive, "SuccessfulExit").Name.LocalName);

        Assert.Equal("integer", ValueAfter(root, "ThrottleInterval").Name.LocalName);
        Assert.True(int.Parse(ValueAfter(root, "ThrottleInterval").Value) >= 10);
    }

    [Fact]
    public void Plist_CapturesStandardErrorAndOut()
    {
        var plist = MacDaemonLaunchAgent.RenderPlist(
            Dotnet, "/staged/Mainguard.Server.dll", "/staged", "/var/logs");
        var root = PlistDict(plist);

        // F63d: a crash before the daemon's own logging pipeline exists printed to a stderr launchd
        // discarded, so "it does not start" had nothing behind it.
        Assert.Equal("/var/logs/launchd.err.log", ValueAfter(root, "StandardErrorPath").Value);
        Assert.Equal("/var/logs/launchd.out.log", ValueAfter(root, "StandardOutPath").Value);
    }

    [Fact]
    public void Plist_JobPath_IncludesHomebrewAndTheMuxersOwnDirectory()
    {
        var plist = MacDaemonLaunchAgent.RenderPlist(
            Dotnet, "/staged/Mainguard.Server.dll", "/staged", "/logs");
        var environment = ValueAfter(PlistDict(plist), "EnvironmentVariables");
        var path = ValueAfter(environment, "PATH").Value.Split(':');

        Assert.Contains("/usr/local/share/dotnet", path);
        Assert.Contains("/opt/homebrew/bin", path);
        Assert.Contains("/usr/bin", path);
        Assert.Equal(Dotnet, ValueAfter(environment, "DOTNET_HOST_PATH").Value);

        // N6: the job tells the daemon it is supervised, so a refusal to start against a data root some
        // other daemon holds exits 0 instead of asking KeepAlive to exec it again every 30 seconds.
        Assert.Equal(
            "launchd",
            ValueAfter(environment, DaemonExitCodes.SupervisorVariable).Value);
    }

    /// <summary>
    /// F63e: the job must run from the staged copy under the data root, never from inside the
    /// <c>.app</c> bundle — a bundle replaced in place while the daemon runs gives a lazily-loading
    /// process a mix of old and new assemblies.
    /// </summary>
    [Fact]
    public void StagedPayloadDirectory_IsUnderTheDataRoot_NotInsideAnAppBundle()
    {
        var staged = MacDaemonLaunchAgent.StagedPayloadDirectory();

        Assert.StartsWith(Mainguard.Git.MainguardPaths.DataRoot(), staged, StringComparison.Ordinal);
        Assert.DoesNotContain(".app/", staged, StringComparison.Ordinal);
    }

    [Fact]
    public void StageIncomingPayload_CopiesOutOfTheSourceTree_Recursively()
    {
        var source = NewPayload("payload", "native");

        var incoming = MacDaemonLaunchAgent.StageIncomingPayload(source);

        Assert.NotNull(incoming);
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(incoming!));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(incoming!, "Mainguard.Server.dll")));
        Assert.Equal(
            "native", File.ReadAllText(Path.Combine(incoming!, "runtimes", "osx-arm64", "native.dylib")));

        // Copied, never symlinked: a link back into the bundle reintroduces the mixed-assembly failure.
        Assert.Null(new FileInfo(Path.Combine(incoming!, "Mainguard.Server.dll")).LinkTarget);

        Assert.True(MacDaemonLaunchAgent.CommitStagedPayload(incoming!));
        Assert.Equal(
            "payload",
            File.ReadAllText(Path.Combine(MacDaemonLaunchAgent.StagedPayloadDirectory(), "Mainguard.Server.dll")));
    }

    /// <summary>
    /// B2: the staging copy must never be written into the directory the launchd job is executing from.
    /// The first version deleted the staged directory and copied into it while the daemon ran out of it —
    /// the mixed-assembly window (e) exists to close, reintroduced at every refresh, and worse than the
    /// original because a lazy load during the copy finds a missing or half-written file. The live payload
    /// is untouched until the rename swap, which happens only while the job is booted out.
    /// </summary>
    [Fact]
    public void StageIncomingPayload_LeavesTheLivePayloadIntact_UntilTheSwap()
    {
        var staged = MacDaemonLaunchAgent.StagedPayloadDirectory();
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "Mainguard.Server.dll"), "running");
        File.WriteAllText(Path.Combine(staged, "OnlyInTheOldBuild.dll"), "old");

        var incoming = MacDaemonLaunchAgent.StageIncomingPayload(NewPayload("next", "native"));

        Assert.NotNull(incoming);
        Assert.NotEqual(Path.GetFullPath(staged), Path.GetFullPath(incoming!));
        Assert.Equal("running", File.ReadAllText(Path.Combine(staged, "Mainguard.Server.dll")));

        Assert.True(MacDaemonLaunchAgent.CommitStagedPayload(incoming!));
        Assert.Equal("next", File.ReadAllText(Path.Combine(staged, "Mainguard.Server.dll")));

        // Replaced wholesale, not merged: a file dropped between versions must not survive as a stale
        // assembly — and the incoming directory is gone, so the next stage starts clean.
        Assert.False(File.Exists(Path.Combine(staged, "OnlyInTheOldBuild.dll")));
        Assert.False(Directory.Exists(MacDaemonLaunchAgent.IncomingPayloadDirectory()));
    }

    /// <summary>
    /// A payload without the daemon assembly is a refusal, not a staging. The updater keys off this null:
    /// kickstarting onto an incomplete payload gives launchd a daemon that crashes on a missing assembly,
    /// and <c>Crashed:true</c> respawns it every 30 s forever.
    /// </summary>
    [Fact]
    public void StageIncomingPayload_RefusesAPayloadWithNoDaemonAssembly()
    {
        var source = Path.Combine(Path.GetTempPath(), "mainguard-stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "something-else.txt"), "not a payload");

        Assert.Null(MacDaemonLaunchAgent.StageIncomingPayload(source));
        Assert.False(Directory.Exists(MacDaemonLaunchAgent.IncomingPayloadDirectory()));
    }

    /// <summary>A source that already IS the staged directory stages nothing and commits nothing.</summary>
    [Fact]
    public void StageIncomingPayload_OnTheStagedDirectoryItself_IsANoOp()
    {
        var staged = MacDaemonLaunchAgent.StagedPayloadDirectory();
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "Mainguard.Server.dll"), "running");

        var incoming = MacDaemonLaunchAgent.StageIncomingPayload(staged);

        Assert.Equal(staged, incoming);
        Assert.True(MacDaemonLaunchAgent.CommitStagedPayload(incoming!));
        Assert.Equal("running", File.ReadAllText(Path.Combine(staged, "Mainguard.Server.dll")));
    }

    /// <summary>A payload tree with the two files every staging assertion here reads.</summary>
    private static string NewPayload(string daemon, string native)
    {
        var source = Path.Combine(Path.GetTempPath(), "mainguard-stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "runtimes", "osx-arm64"));
        File.WriteAllText(Path.Combine(source, "Mainguard.Server.dll"), daemon);
        File.WriteAllText(Path.Combine(source, "runtimes", "osx-arm64", "native.dylib"), native);
        return source;
    }

    private static XElement PlistDict(string plist) =>
        XDocument.Parse(plist).Root!.Element("dict")!;

    /// <summary>A plist dict is a flat key/value sequence — the value is the element after its key.</summary>
    private static XElement ValueAfter(XElement dict, string key)
    {
        var element = dict.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "key" && e.Value == key)
            ?.ElementsAfterSelf()
            .FirstOrDefault();

        Assert.NotNull(element);
        return element!;
    }
}
