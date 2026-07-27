using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The regression guard for the defect this suite's sibling fixes: <b>no production code may ask
/// Docker.DotNet to attach an exec's stdin.</b>
///
/// <para><b>What actually broke.</b> Docker.DotNet 3.125.15 is the latest published version and its
/// hijacked-stream path does not work against a modern Docker engine. Measured against Engine 29.4.3, on
/// a container with the jail's exact shape, an exec created with <c>AttachStdin = true</c> delivers
/// neither the payload nor the half-close: the in-jail file is created and left <b>0 bytes</b>, and the
/// exec never leaves <c>Running</c>, so the wait never returns. Every secret on the spawn path travels
/// on exec stdin, so the whole agent-spawn path fails the moment <c>MainguardEnv</c>'s end-of-life
/// Docker 20.10.24 is upgraded — with a 30-second timeout as the only symptom. Stdin is now carried by
/// <c>DockerSocketExecStdinTransport</c>, which speaks the three exec calls to the daemon directly.</para>
///
/// <para><b>Why a source scan.</b> The property is "this API is not called anywhere in the shipped
/// closure", which no runtime assertion can establish — a reflection probe only sees the paths a test
/// happens to drive, and the reintroduction this guards against would be in a path no unit test
/// exercises (a new adapter, a new bootstrap step). Reading the sources is the only check whose failure
/// means what it says.</para>
///
/// <para><b>Every step is anchored so the guard cannot pass vacuously.</b> A source scan is exactly the
/// shape of test that lies: point it at the wrong directory, or write a regex that matches nothing, and
/// it reports a clean bill of health for a repository it never read. So it asserts, in order, that it
/// found the repository root, that it read a plausible number of production files, that the specific
/// files known to drive Docker are among them, and — the real control — that the SAME matcher finds the
/// sibling initializer <c>AttachStdout = true</c>, which production legitimately does use. If the
/// matcher can see <c>AttachStdout = true</c> in these files, it can see <c>AttachStdin = true</c>.</para>
/// </summary>
public class DockerStdinRegressionGuardTests
{
    /// <summary>The C# object-initializer form. This is the ONLY way to set the property on
    /// Docker.DotNet's <c>ContainerExecCreateParameters</c>, so matching it matches every way to ask that
    /// library to attach stdin.</summary>
    private static readonly Regex AttachStdinTrue = new(@"AttachStdin\s*=\s*true", RegexOptions.Compiled);

    /// <summary>The positive control: the same shape, on a sibling field production really does set.</summary>
    private static readonly Regex AttachStdoutTrue = new(@"AttachStdout\s*=\s*true", RegexOptions.Compiled);

    /// <summary>Docker.DotNet's exec-create request type. Its presence is what turns a mention of
    /// <c>AttachStdin</c> into a use of the broken library path (see
    /// <see cref="NoProductionCode_NamesExecStdin_AlongsideDockerDotNetsExecParameters"/>).</summary>
    private const string DockerDotNetExecParameters = "ContainerExecCreateParameters";

    /// <summary>Everything that ships. Test projects are deliberately absent: a test may legitimately
    /// construct the parameters to assert something about them.</summary>
    private static readonly string[] ProductionProjects =
    {
        "Mainguard.Agents",
        "Mainguard.Agents.UI",
        "Mainguard.App.Shell",
        "Mainguard.Client.App",
        "Mainguard.Git",
        "Mainguard.Pro.App",
        "Mainguard.Protos",
        "Mainguard.Server",
        "Mainguard.UI",
        Path.Combine("installer", "Mainguard.Installer"),
        Path.Combine("installer", "Mainguard.Installer.Elevated"),
        Path.Combine("installer", "Mainguard.Uninstall"),
    };

    [Fact]
    public void NoProductionCode_AsksDockerDotNet_ToAttachExecStdin()
    {
        var sources = ProductionSources();

        // --- anchors, before the assertion that matters -------------------------------------------
        Assert.True(sources.Count > 200,
            $"only {sources.Count} production source files were found — the scan is not looking at the "
            + "repository, so a clean result would mean nothing.");

        var names = sources.Select(f => Path.GetFileName(f.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "DockerSandboxEngine.cs", "EgressProxyConfigurator.cs", "ExecStdinTransport.cs" })
        {
            Assert.True(names.Contains(required),
                $"'{required}' was not among the {sources.Count} scanned files — the scan missed the very "
                + "directory the Docker exec calls live in.");
        }

        // The control. Production sets AttachStdout on its read-only execs; if the matcher cannot find
        // THAT, it could not have found AttachStdin either and the verdict below is worthless.
        var controlHits = sources.Where(f => AttachStdoutTrue.IsMatch(f.Code)).Select(f => f.Relative).ToList();
        Assert.True(controlHits.Count >= 2,
            "the positive control failed: 'AttachStdout = true' was found in "
            + $"{controlHits.Count} production file(s). Production creates several stdout-attached execs, "
            + "so this matcher is not seeing the source it claims to be checking.");

        // --- the property -------------------------------------------------------------------------
        var offenders = sources.Where(f => AttachStdinTrue.IsMatch(f.Code)).Select(f => f.Relative).ToList();
        Assert.True(offenders.Count == 0,
            "production code asks Docker.DotNet to attach exec stdin in: " + string.Join(", ", offenders)
            + ". That library does not deliver stdin against a modern Docker engine — the bytes never "
            + "arrive, the exec never finishes, and the only symptom is a 30-second timeout on every "
            + "agent spawn. Route the write through IExecStdinTransport instead.");
    }

    /// <summary>
    /// Closes the one loophole the regex above would otherwise leave: <c>AttachStdin</c> is also a wire
    /// field name, so a file may legitimately contain the token in some OTHER syntax (the transport's own
    /// JSON body says <c>AttachStdin: true</c> as a named argument). What must never happen is that token
    /// appearing in a file that also constructs Docker.DotNet's exec-create parameters — that combination
    /// is the broken path under any spelling, including ones this suite's regex does not anticipate.
    /// </summary>
    [Fact]
    public void NoProductionCode_NamesExecStdin_AlongsideDockerDotNetsExecParameters()
    {
        var sources = ProductionSources();

        // Control: production DOES construct Docker.DotNet exec parameters (the read-only execs, which
        // work fine). If this finds nothing, the assertion below is testing an empty set.
        var usesDockerExec = sources.Where(f => f.Code.Contains(DockerDotNetExecParameters, StringComparison.Ordinal)).ToList();
        Assert.True(usesDockerExec.Count >= 2,
            $"only {usesDockerExec.Count} production file(s) mention {DockerDotNetExecParameters}; production "
            + "creates stdout-attached execs in several places, so this scan is not seeing real source.");

        var offenders = usesDockerExec
            .Where(f => f.Code.Contains("AttachStdin", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"these files name AttachStdin while also building {DockerDotNetExecParameters}: "
            + string.Join(", ", offenders)
            + ". Docker.DotNet cannot deliver exec stdin against a modern engine; use IExecStdinTransport.");
    }

    [Fact]
    public void TheGuard_WouldFail_IfAnExecStdinAttachCameBack()
    {
        // Non-vacuity, proved rather than argued: the same matcher, over the same production sources,
        // plus one synthetic file of exactly the shape being banned. If this does NOT trip, the test
        // above is decoration.
        var sources = ProductionSources();
        sources.Add(new Source(
            "(synthetic)", "(synthetic)",
            "var p = new ContainerExecCreateParameters { AttachStdin = true, AttachStdout = true };"));

        var offenders = sources.Where(f => AttachStdinTrue.IsMatch(f.Code)).Select(f => f.Relative).ToList();
        Assert.Equal(new[] { "(synthetic)" }, offenders);
    }

    /// <param name="Code">The file with whole-line comments removed. Necessary, and discovered the
    /// honest way: the first run of this guard failed on its own explanatory doc comment. Only WHOLE
    /// comment lines are dropped — a trailing <c>// …</c> is left in place, so the guard still fails
    /// closed on a line that mixes code and prose rather than being taught to look away.</param>
    private sealed record Source(string Path, string Relative, string Code);

    private static string StripCommentLines(string text)
    {
        var kept = text
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("//", StringComparison.Ordinal)
                    && !trimmed.StartsWith("*", StringComparison.Ordinal)
                    && !trimmed.StartsWith("/*", StringComparison.Ordinal);
            });

        return string.Join('\n', kept);
    }

    private static List<Source> ProductionSources()
    {
        var root = RepoRoot();
        var sources = new List<Source>();

        foreach (var project in ProductionProjects)
        {
            var dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                // Loud, not skipped: a renamed or moved project must not silently shrink the scan.
                throw new DirectoryNotFoundException(
                    $"The production project directory '{project}' does not exist under '{root}'. Update "
                    + $"{nameof(ProductionProjects)} — a guard that scans a directory that is not there "
                    + "passes for the wrong reason.");
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                sources.Add(new Source(file, relative, StripCommentLines(File.ReadAllText(file))));
            }
        }

        return sources;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Path.GetDirectoryName(dir);

        // Never fall back to BaseDirectory: that directory has no production sources, so the scan would
        // find nothing and the guard would report success for a repository it never opened.
        return dir ?? throw new DirectoryNotFoundException(
            $"Could not find Mainguard.slnx above '{AppContext.BaseDirectory}', so the production sources "
            + "could not be scanned. This is a failure, not a skip.");
    }
}
