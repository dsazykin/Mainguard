using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Adoption's second half (audit F6), the stop-vs-reconcile race (F14), and the one thing nothing in this
/// assembly tested at all: the reconciler <b>hosted service</b> actually running.
///
/// <para>The Docker-witnessed proof that a re-bound CLI is a real, steerable process lives in
/// <see cref="RestartSurvivalDockerTests"/>; these pin the daemon-side decisions that get it there.</para>
/// </summary>
public sealed class AdoptionReattachTests
{
    private const string Repo = "adoptrepohash";

    private static AgentSessionReconciler Reconciler(
        AgentSessionStore store,
        IReadOnlyList<AgentContainerState> containers,
        Action<AgentSession>? onAdopted = null)
        => new(store, _ => Task.FromResult(containers), onAdopted: onAdopted);

    // ================= F6: the hook fires for every jail that becomes re-attachable =================

    /// <summary>
    /// A running jail with no record is adopted and handed to the re-attach hook. This was already true
    /// for the IPC endpoint; what the hook does with it now also includes the CLI, which is the half that
    /// did not exist.
    /// </summary>
    [Fact]
    public async Task AdoptingARunningJail_HandsItToTheReattachHook()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var seen = new List<string>();

        await Reconciler(
            store,
            new[] { new AgentContainerState("a1", Repo, "jail-1", Running: true, Paused: false) },
            onAdopted: s => seen.Add(s.Id)).ReconcileAsync();

        Assert.Equal(new[] { "a1" }, seen);
    }

    /// <summary>
    /// <b>The paused case, which is the interesting one.</b> <c>docker exec</c> into a SIGSTOPped
    /// container blocks, so the adoption pass cannot re-bind a CLI into a jail it adopts frozen — and if
    /// nothing re-tried when it thawed, the agent would be adopted, resumed, and still unsteerable. The
    /// pass that clears the pause is the pass that can re-attach, so it does.
    /// </summary>
    [Fact]
    public async Task AJailAdoptedPaused_IsHandedToTheHookAgain_WhenItThaws()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var seen = new List<string>();

        var frozen = new[] { new AgentContainerState("a1", Repo, "jail-1", Running: false, Paused: true) };
        await Reconciler(store, frozen, onAdopted: s => seen.Add(s.Id)).ReconcileAsync();
        Assert.Equal(new[] { "a1" }, seen); // adopted — the hook decides it cannot bind yet

        var thawed = new[] { new AgentContainerState("a1", Repo, "jail-1", Running: true, Paused: false) };
        var report = await Reconciler(store, thawed, onAdopted: s => seen.Add(s.Id)).ReconcileAsync();

        Assert.Contains("a1", report.Corrected);
        Assert.Equal(new[] { "a1", "a1" }, seen);
    }

    /// <summary>A hook that throws is one agent's problem, never the pass's.</summary>
    [Fact]
    public async Task AThrowingReattachHook_DoesNotFailThePass()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());

        var report = await Reconciler(
            store,
            new[] { new AgentContainerState("a1", Repo, "jail-1", Running: true, Paused: false) },
            onAdopted: _ => throw new InvalidOperationException("no adapter")).ReconcileAsync();

        Assert.Contains("a1", report.Adopted);
        Assert.NotNull(store.Find(Repo, "a1"));
    }

    /// <summary>
    /// The engine's own reading of a freeze must not overwrite a more specific one. The axis records WHO
    /// froze the jail and every release path keys on it, so a rehydrated "a human paused it" surviving one
    /// pass and being flattened by the next would re-tell the restart's lie 30 seconds later.
    /// </summary>
    [Fact]
    public async Task TheDriftPass_DoesNotOverwriteAMoreSpecificFreezeReason()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var key = new AgentSessionKey(Repo, "a1");
        store.Spawn("claude-code", agentId: "a1", repoHash: Repo);
        store.AttachSandbox(key, "jail-1");
        store.MarkFrozen(key, SandboxKillTarget.PausedByKillReason);
        store.MarkState(key, "Working", null); // the merge queue's reflection rewrote the word

        await Reconciler(
            store,
            new[] { new AgentContainerState("a1", Repo, "jail-1", Running: false, Paused: true) })
            .ReconcileAsync();

        Assert.Equal(AgentSessionReconciler.PausedState, store.Find(key)!.State);
        Assert.Equal(SandboxKillTarget.PausedByKillReason, store.FrozenReason(key));
    }

    // ================= F14: the stop-vs-reconcile race ==============================================

    /// <summary>
    /// <b>Audit F14.</b> A stop removes the record first and then spends seconds harvesting the CLI's
    /// logins out of the jail before the container goes. A pass landing in that window sees a live
    /// container with no record — the adoption shape exactly — and resurrects a session nothing will ever
    /// remove, which then refuses the entry's Resume ("already has a live agent") until the reaper clears
    /// it half an hour later. "No record" is only evidence of an orphan when nobody is mid-removal.
    /// </summary>
    [Fact]
    public async Task AJailWhoseTeardownIsInFlight_IsNotAdopted()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var key = new AgentSessionKey(Repo, "stopping");
        var containers = new[]
        {
            new AgentContainerState("stopping", Repo, "jail-s", Running: true, Paused: false),
        };

        using (store.BeginTeardown(key))
        {
            var report = await Reconciler(store, containers).ReconcileAsync();

            Assert.Empty(report.Adopted);
            Assert.Null(store.Find(key));
        }

        // Once the teardown is finished the guard is gone. A container that really did outlive its stop
        // is still adopted on the next pass — the suppression is a window, not an exemption.
        var after = await Reconciler(store, containers).ReconcileAsync();
        Assert.Contains("stopping", after.Adopted);
    }

    /// <summary>The teardown scope is repo-scoped like everything else: stopping repo A's
    /// <c>pr-7</c> must not stop the reconciler adopting repo B's.</summary>
    [Fact]
    public async Task TheTeardownGuard_IsScopedToOneRepo()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());

        using var _ = store.BeginTeardown(new AgentSessionKey("repo-a", "pr-7"));
        var report = await Reconciler(store, new[]
        {
            new AgentContainerState("pr-7", "repo-a", "jail-a", Running: true, Paused: false),
            new AgentContainerState("pr-7", "repo-b", "jail-b", Running: true, Paused: false),
        }).ReconcileAsync();

        Assert.Equal(new[] { "pr-7" }, report.Adopted);
        Assert.Null(store.Find("repo-a", "pr-7"));
        Assert.NotNull(store.Find("repo-b", "pr-7"));
    }

    // ================= The hosted service, actually running ========================================

    /// <summary>
    /// <b>The reconciler's own hosted service, enabled, in-proc.</b> This assembly disables
    /// <see cref="AgentSessionReconcilerService"/> for every test (its module initializer does, because
    /// the container engine is machine-wide and an in-proc daemon would otherwise adopt the developer's
    /// real jails), with the consequence that the adoption path had no in-proc coverage anywhere: the
    /// pass was tested, the SERVICE that runs it was not, and a service that returns early runs nothing.
    ///
    /// <para>The switch is flipped for the length of this test only, and the reconciler it drives lists a
    /// container set the test wrote — so the machine's real jails are neither read nor written, which is
    /// the concern the assembly-wide disable exists for.</para>
    /// </summary>
    [Fact]
    public async Task TheReconcilerHostedService_AdoptsASurvivingJail_WhenItIsEnabled()
    {
        var previous = Environment.GetEnvironmentVariable(AgentSessionReconcilerService.DisableVariable);
        Environment.SetEnvironmentVariable(AgentSessionReconcilerService.DisableVariable, null);
        try
        {
            Assert.False(AgentSessionReconcilerService.Disabled, "the switch did not come off");

            var store = new AgentSessionStore(new InMemoryAuditLog());
            var reattached = new List<string>();
            var service = new AgentSessionReconcilerService(
                Reconciler(
                    store,
                    new[] { new AgentContainerState("boot-1", Repo, "jail-b", Running: true, Paused: false) },
                    onAdopted: s => reattached.Add(s.Id)),
                NullLogger<AgentSessionReconcilerService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                // The first pass runs immediately (the interval delay comes after it), so this is a wait
                // for a pass that is already in flight, not a poll of a timer.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (service.LastReport is null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            Assert.NotNull(service.LastReport);
            Assert.Contains("boot-1", service.LastReport!.Adopted);
            Assert.Equal("jail-b", store.Find(Repo, "boot-1")!.ContainerId);
            Assert.Equal(new[] { "boot-1" }, reattached);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentSessionReconcilerService.DisableVariable, previous);
        }
    }

    // ================= The exit tail (jail-controlled text in the audit chain) ======================

    /// <summary>
    /// The <c>cli_exited</c> audit event and the dead session's reason carry the CLI's last output, which
    /// is text the jail's occupant chooses. It went in verbatim: ANSI CSI/OSC sequences (including the
    /// OSC 52 clipboard write the daemon suppresses everywhere else), C0 controls, and newlines that split
    /// a line-oriented sink into records it did not write. The diagnosis is the words; none of the rest of
    /// it is.
    /// </summary>
    [Theory]
    // A plain diagnosis survives exactly.
    [InlineData("Not logged in. Run `claude login`.", "Not logged in. Run `claude login`.")]
    // CSI colour/erase sequences go; the text between them stays and does not run together.
    [InlineData("[31mfatal:[0m [2Kno TTY", "fatal: no TTY")]
    // An OSC 52 clipboard write, terminated by BEL, is not text at all. Removing a sequence must not
    [InlineData("before]52;c;aGVsbG8=after", "beforeafter")]
    // manufacture a separator, or `foo<ESC>[0mbar` would come back as two words. Same for ST-terminated.
    [InlineData("a]0;title\\b", "ab")]
    // Newlines and tabs fold to single spaces so one exit is one audit record.
    [InlineData("line one\r\n\tline two\n\n\nline three", "line one line two line three")]
    // A bidi override cannot reorder what a human reads out of an audit entry.
    [InlineData("safe‮evil", "safe evil")]
    // An unterminated CSI swallows its own tail rather than leaking the parameter bytes.
    [InlineData("head [38;5;", "head")]
    public void TheExitTail_IsStrippedOfEverythingThatIsNotText(string raw, string expected)
        => Assert.Equal(expected, AgentCliBinder.SanitizeExitTail(raw));

    /// <summary>The cap still holds after the stripping, and it is applied to the TEXT — a CLI that spends
    /// its dying breath repainting the screen must not spend the whole budget on control bytes.</summary>
    [Fact]
    public void TheExitTail_IsCappedAfterStripping()
    {
        var noisy = string.Concat(Enumerable.Repeat("[K x", 5000));

        var sanitized = AgentCliBinder.SanitizeExitTail(noisy);

        Assert.True(sanitized.Length <= AgentCliBinder.ExitTailChars, $"length {sanitized.Length}");
        Assert.DoesNotContain('', sanitized);
        Assert.StartsWith("x x x", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrAllControlTail_IsEmpty()
    {
        Assert.Equal(string.Empty, AgentCliBinder.SanitizeExitTail(null));
        Assert.Equal(string.Empty, AgentCliBinder.SanitizeExitTail("[2J[H\r\n"));
    }

    // ================= F7: the leader's input gate, made real ======================================

    /// <summary>
    /// <b>Audit F7.</b> <c>SessionLeader.PauseInput</c>/<c>ResumeInput</c> gated nothing: the only
    /// non-test reader of <c>IsPaused</c> was the kill switch computing whether IT had closed the gate,
    /// for its own release ledger. So the gateway's 429 / budget pause and the yield window wrote a flag
    /// and keystrokes reached the CLI regardless — a pause that paused nothing.
    ///
    /// <para>Dropped, not buffered, and that is deliberate: the gate is closed for a rate-limited agent
    /// and for the yield window, and a keystroke held and replayed minutes later arrives in a CLI whose
    /// screen has moved on — which is how an Enter lands on a permission dialog nobody was looking at.</para>
    /// </summary>
    [Fact]
    public async Task ThePausedInputGate_DropsHumanKeystrokes_AndResumingRestoresThem()
    {
        var pty = new RecordingSession();
        var paused = false;
        using var bound = new BoundTerminalSession(
            "a1", pty, isInputLocked: null, isInputPaused: () => paused);

        await bound.WriteInputAsync(Encoding.UTF8.GetBytes("before"), CancellationToken.None);
        paused = true;
        await bound.WriteInputAsync(Encoding.UTF8.GetBytes("swallowed"), CancellationToken.None);
        paused = false;
        await bound.WriteInputAsync(Encoding.UTF8.GetBytes("after"), CancellationToken.None);

        Assert.Equal("beforeafter", pty.Written());
    }

    /// <summary>
    /// The exemption, for the same reason <c>send_worker_prompt</c> does not consult the terminal input
    /// lock: the gate severs a HUMAN's keyboard, and honouring it on the daemon's own sanctioned channel
    /// would make the one way of telling a paused agent anything impossible exactly when it matters.
    /// </summary>
    [Fact]
    public async Task TheDaemonsOwnSanctionedWrite_IsNotGatedByThePausedInput()
    {
        var pty = new RecordingSession();
        using var bound = new BoundTerminalSession(
            "a1", pty, isInputLocked: null, isInputPaused: () => true);

        await bound.SubmitLineAndAwaitOutputAsync(
            Encoding.UTF8.GetBytes("steer"), Encoding.UTF8.GetBytes("\r"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Equal("steer\r", pty.Written());
    }

    private sealed class RecordingSession : ITerminalSession
    {
        private readonly System.IO.MemoryStream _io = new();

        public System.IO.Stream IO => _io;

        public Task<int> ExitCode { get; } = new TaskCompletionSource<int>().Task;

        public string Written() => Encoding.UTF8.GetString(_io.ToArray());

        public void Resize(int cols, int rows)
        {
        }

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }
}
