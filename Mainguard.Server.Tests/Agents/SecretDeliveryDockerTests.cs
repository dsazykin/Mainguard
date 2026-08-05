using System;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The RequiresDocker leg for secret delivery into a real hardened jail.
///
/// <para><b>The defect.</b> Every secret on the spawn path travels on an exec's stdin, and Docker.DotNet
/// 3.125.15 — the latest published version — cannot deliver it against a modern engine. Measured on
/// Engine 29.4.3 against a container with the jail's exact shape: the bytes never arrive, the half-close
/// never propagates, the in-jail file is created and left <b>0 bytes</b>, and the exec never leaves
/// <c>Running</c>, so the wait never returns. <c>MainguardEnv</c> still ships the end-of-life Docker
/// 20.10.24, so this is latent until the VM is upgraded — at which point every spawn fails with a
/// 30-second timeout as its only symptom. <see cref="DockerSocketExecStdinTransport"/> carries stdin now.</para>
///
/// <para><b>The assertions are framed, not substring-matched.</b> "mode 0400 owned by 1000" is exactly
/// the shape of check that passes when the probe printed nothing at all, so the in-jail probe wraps each
/// fact in its own sentinel and the test asserts on a fully-formed record. A command that fails to run
/// produces no frame and fails the test with that stated as the reason, rather than looking like a file
/// that merely does not exist. The payload is a per-run random nonce, so a stale file from a previous run
/// (or a file that "happened to already be 0400") cannot satisfy it either.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class SecretDeliveryDockerTests
{
    /// <summary>The jail path the credential env-file lands on (tmpfs, mode 0711, inside a read-only
    /// rootfs — the combination that makes this hard).</summary>
    private const string SecretPath = CredTmpfsSpec.DefaultCredentialPath;

    /// <summary>The OOB session key's jail path — owned by the SUPERVISOR uid, not the agent (G2
    /// control 1), which is what makes the two writes a real test of per-owner delivery.</summary>
    private const string OobKeyPath = CredTmpfsSpec.DefaultOobKeyPath;

    [RequiresDockerFact]
    public async Task Spawn_DeliversBothSecrets_At0400_ToTheirOwnUids_IntoTheJailsTmpfs()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        // A per-run nonce: neither a leftover file nor a coincidentally-0400 file can satisfy this.
        var nonce = "MG-SECRET-" + Guid.NewGuid().ToString("N");

        // The whole production spawn path, not a hand-rolled call: provision → egress → create → start →
        // deliver secrets. Before this fix it did not get past secret delivery against Engine 29.
        var handle = await fixture.SpawnAsync(
            agentId: "secret-delivery-1",
            agentEnv: new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = nonce },
            ct: ct);

        var credential = await ReadFileFactsAsync(fixture, handle.ContainerId, SecretPath, ct);

        Assert.True(credential.Ran,
            $"the in-jail probe produced no {FactFrame} frame at all, so nothing about '{SecretPath}' was "
            + $"observed — the probe did not run. Raw output: '{credential.Raw}'");
        Assert.True(credential.Exists,
            $"'{SecretPath}' does not exist in the jail although the spawn reported success. That is the "
            + "silent-failure this test exists for. Raw output: " + credential.Raw);

        // The exact bytes THIS spawn sent — not a truncation, and not a file that was already there.
        Assert.Contains(nonce, credential.Content, StringComparison.Ordinal);
        Assert.NotEqual("0", credential.Bytes);
        Assert.Equal("400", credential.Mode); // G-13: readable by its owner alone
        Assert.Equal("1000", credential.Uid); // and that owner is the agent uid

        // The gid is deliberately NOT asserted: the in-jail shell chowns the OWNER only, and at mode
        // 0400 the group bits are empty, so the group has no access whatever gid it carries. Asserting
        // it would pin an incidental rather than the control.

        // The second write goes to a DIFFERENT uid. Asserting only the first would pass against an
        // implementation that ignored the owner argument entirely and gave everything to the agent.
        //
        // Probed AS THE SUPERVISOR, because K now lives in the supervisor uid's own 0700 directory: the
        // agent cannot see it at all, and asking the agent would conflate "not delivered" with "properly
        // hidden". The two questions are asked separately, of the two uids that can answer them.
        var oob = await ReadFileFactsAsync(fixture, handle.ContainerId, OobKeyPath, ct, asUid: 1001);
        Assert.True(oob.Ran, "the in-jail probe did not run for the OOB key path. Raw: " + oob.Raw);
        Assert.True(oob.Exists, $"'{OobKeyPath}' was not delivered. Raw: " + oob.Raw);
        Assert.Equal("400", oob.Mode);
        Assert.Equal("1001", oob.Uid);

        // G2 control 1, stated as the relationship rather than as two constants: the OOB key must not be
        // readable by the uid the (potentially prompt-injected) agent CLI runs as.
        Assert.NotEqual(credential.Uid, oob.Uid);

        // …and demonstrated, not merely inferred from the modes: the SAME probe, run as the agent, cannot
        // reach K — it does not even resolve, because the supervisor's directory denies the agent uid
        // before the file's own 0400 is ever consulted. The control now holds one level earlier than it
        // did when both secrets shared a traversable 0711 directory.
        var oobFromAgent = await ReadFileFactsAsync(fixture, handle.ContainerId, OobKeyPath, ct);
        Assert.True(oobFromAgent.Ran,
            "the in-jail probe did not run as the agent, so 'the agent cannot reach K' is unmeasured. Raw: "
            + oobFromAgent.Raw);
        Assert.False(oobFromAgent.Exists,
            "the agent uid can reach the supervisor-owned OOB key. Raw: " + oobFromAgent.Raw);
        Assert.DoesNotContain(nonce, oobFromAgent.Raw, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task Spawn_LeavesNoStagingFileBehind()
    {
        // The staged-write shell publishes with an atomic rename; a '<path>.partial' still sitting there
        // would mean the rename did not happen and what is at the destination came from somewhere else.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        var handle = await fixture.SpawnAsync(agentId: "secret-delivery-2", ct: ct);

        var staged = await ReadFileFactsAsync(fixture, handle.ContainerId, SecretPath + ".partial", ct);
        Assert.True(staged.Ran, "the in-jail probe did not run, so 'is a staging file left behind?' is unanswered.");
        Assert.False(staged.Exists, $"'{SecretPath}.partial' survived a successful write. Raw: {staged.Raw}");

        // Non-vacuity for the assertion above: the same probe, in the same jail, DOES report Exists for
        // the destination — so `Exists == false` above means "absent", not "the probe always says false".
        var destination = await ReadFileFactsAsync(fixture, handle.ContainerId, SecretPath, ct);
        Assert.True(destination.Exists,
            "the control failed: the probe reports the destination absent too, so its 'absent' verdict "
            + "for the staging file proves nothing. Raw: " + destination.Raw);
    }

    /// <summary>
    /// Pins <b>why</b> the obvious replacement is not available, so nobody re-derives it.
    ///
    /// <para><c>PUT /containers/{id}/archive</c> is the natural way to put a file in a container, and it
    /// cannot be used here. Docker refuses to extract into a container whose rootfs is read-only unless
    /// the destination is inside a <i>volume or bind mount</i>, and <c>HostConfig.Tmpfs</c> entries are
    /// neither — so both jail secret destinations are rejected outright. (Declaring the same tmpfs
    /// through <c>HostConfig.Mounts</c> instead gets past that check and is worse: the call then reports
    /// success while the bytes land in the image layer underneath the tmpfs, where the container never
    /// sees them.) If this test ever starts failing because Docker began allowing the extraction, the
    /// archive API becomes a genuine option and this whole transport can be revisited — which is the
    /// only reason to assert a limitation rather than merely write it down.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ArchiveExtraction_IsRefusedByDocker_ForAReadOnlyRootfsJail()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var fixture = new SandboxFixture();

        var handle = await fixture.SpawnAsync(agentId: "secret-delivery-3", ct: ct);

        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, SecretPath.TrimStart('/') + ".archive")
            {
                Mode = (UnixFileMode)0b100_000_000, // 0400
                Uid = 1000,
                Gid = 1000,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("would-be-secret")),
            });
        }

        tar.Position = 0;

        var ex = await Assert.ThrowsAsync<DockerApiException>(() =>
            fixture.Docker.Containers.ExtractArchiveToContainerAsync(
                handle.ContainerId,
                new ContainerPathStatParameters { Path = "/", AllowOverwriteDirWithFile = false },
                tar, ct));

        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And it really did not write: a refusal that had partially applied would be worse than either.
        var observed = await ReadFileFactsAsync(fixture, handle.ContainerId, SecretPath + ".archive", ct);
        Assert.True(observed.Ran, "the in-jail probe did not run, so the 'nothing was written' claim is unverified.");
        Assert.False(observed.Exists, "the archive extraction was refused but still wrote. Raw: " + observed.Raw);
    }

    // ---- the framed in-jail probe ---------------------------------------------------------------

    private const string FactFrame = "MGFACT[";

    /// <summary>What the jail says about one path. <paramref name="Ran"/> distinguishes "the probe did
    /// not execute" from "the file is not there" — the two outcomes that look identical to a naive
    /// substring assertion, and the reason every field below can be trusted.</summary>
    private sealed record FileFacts(
        bool Ran, bool Exists, string Content, string Bytes, string Mode, string Uid, string Gid, string Raw);

    /// <param name="asUid">The uid to run the probe as. Null = the container's own user (the agent).</param>
    private static async Task<FileFacts> ReadFileFactsAsync(
        SandboxFixture fixture, string containerId, string path, CancellationToken ct, int? asUid = null)
    {
        // The frame opener is printed by the shell itself, unconditionally, BEFORE anything can fail —
        // so a missing frame proves the probe never ran rather than that the answer was "no".
        var script =
            "p=\"$1\"; printf '" + FactFrame + "'; "
            + "if [ -e \"$p\" ]; then "
            + "  printf 'EXISTS|%s|%s|%s|%s|%s' "
            + "    \"$(cat \"$p\" 2>/dev/null)\" "
            + "    \"$(wc -c < \"$p\" 2>/dev/null | tr -d ' ')\" "
            + "    \"$(stat -c %a \"$p\" 2>/dev/null)\" "
            + "    \"$(stat -c %u \"$p\" 2>/dev/null)\" "
            + "    \"$(stat -c %g \"$p\" 2>/dev/null)\"; "
            + "else printf 'ABSENT|||||'; fi; "
            + "printf ']'";

        // Read-only exec — no stdin. This probe must not depend on the very channel under test, or a
        // broken transport would take the measurement down with it and report nothing at all.
        //
        // The uid matters now that each secret lives in its OWNER'S own 0700 directory: a probe run as
        // the agent reports the supervisor's key ABSENT, which is the isolation working rather than a
        // delivery failure. So "was it delivered?" is asked as the owner, and "can the agent get it?"
        // is asked separately, as the agent — see the caller.
        var raw = await ProbeAsAsync(fixture, containerId, script, path, asUid, ct).ConfigureAwait(false);
        var start = raw.IndexOf(FactFrame, StringComparison.Ordinal);
        if (start < 0)
            return new FileFacts(false, false, "", "", "", "", "", raw);

        start += FactFrame.Length;
        var end = raw.IndexOf(']', start);
        if (end < 0)
            return new FileFacts(false, false, "", "", "", "", "", raw);

        var parts = raw[start..end].Split('|');
        var exists = parts.Length > 0 && parts[0] == "EXISTS";
        return new FileFacts(
            Ran: true,
            Exists: exists,
            Content: parts.Length > 1 ? parts[1] : "",
            Bytes: parts.Length > 2 ? parts[2] : "",
            Mode: parts.Length > 3 ? parts[3] : "",
            Uid: parts.Length > 4 ? parts[4] : "",
            Gid: parts.Length > 5 ? parts[5] : "",
            Raw: raw);
    }

    /// <summary>Runs the probe as a chosen uid. The engine's own <c>ExecAsync</c> deliberately sets no
    /// user (every jail exec is the agent), so the per-owner read goes through the Docker client the
    /// fixture already exposes rather than widening a production signature for a test.</summary>
    private static async Task<string> ProbeAsAsync(
        SandboxFixture fixture, string containerId, string script, string path, int? asUid, CancellationToken ct)
    {
        if (asUid is null)
        {
            var engineResult = await fixture.Engine.ExecAsync(
                containerId, new[] { "sh", "-c", script, "sh", path }, ct).ConfigureAwait(false);
            return engineResult.Stdout + engineResult.Stderr;
        }

        var exec = await fixture.Docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = asUid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new List<string> { "sh", "-c", script, "sh", path },
        }, ct).ConfigureAwait(false);

        using var stream = await fixture.Docker.Exec
            .StartAndAttachContainerExecAsync(exec.ID, tty: false, ct).ConfigureAwait(false);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
        return stdout + stderr;
    }
}
