using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-48 anti-zombie tests for the resume Scheduled Task lifecycle (the 2026-07-14 incident class: a
/// stale elevated ONLOGON task silently re-running the whole OOBE at every logon). The guard's
/// decisions are proven over a scripted schtasks runner; the real process execution is Windows-only.
/// </summary>
public class ResumeTaskGuardTests
{
    private const string CurrentExe = @"C:\Apps\Mainguard\Mainguard.App.exe";

    private static string TaskXml(string command) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author">
            <Exec>
              <Command>"{command}"</Command>
              <Arguments>--resume</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    // ---- the sweep decision matrix ----

    [Fact]
    public void Sweep_NoTaskRegistered_DoesNothing()
    {
        var runner = new ScriptedSchtasks(queryExit: 1, queryXml: "ERROR: The system cannot find the file specified.");
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.RebootPending, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.NotRegistered, result);
        Assert.DoesNotContain(runner.Invocations, args => args.Contains("/Delete"));
    }

    [Fact]
    public void Sweep_LaunchedByResumeTask_SelfDeletesEvenWhileRebootPending()
    {
        // The task just fired us — its purpose is served; FIRST action is unregistering it, so an
        // abandoned resumed setup leaves nothing behind.
        var runner = new ScriptedSchtasks(queryExit: 0, queryXml: TaskXml(CurrentExe), deleteExit: 0);
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.RebootPending, launchedByResumeTask: true, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.Deleted, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OobeStage.Diagnostics)]
    [InlineData(OobeStage.EnableFeatures)]
    [InlineData(OobeStage.Resumed)]
    [InlineData(OobeStage.ImportVm)]
    [InlineData(OobeStage.Done)]
    public void Sweep_AnyStageExceptRebootPending_DeletesTheTask(OobeStage? stage)
    {
        var runner = new ScriptedSchtasks(queryExit: 0, queryXml: TaskXml(CurrentExe), deleteExit: 0);
        var result = new ResumeTaskGuard(runner.Run).Sweep(stage, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.Deleted, result);
    }

    [Fact]
    public void Sweep_RebootPending_OwnTask_IsKept()
    {
        // The ONE legitimate window: a reboot is genuinely pending and the registration is ours.
        var runner = new ScriptedSchtasks(queryExit: 0, queryXml: TaskXml(CurrentExe));
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.RebootPending, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.KeptAwaitingReboot, result);
        Assert.DoesNotContain(runner.Invocations, args => args.Contains("/Delete"));
    }

    [Fact]
    public void Sweep_RebootPending_ForeignTask_IsDeleted()
    {
        // The incident shape: the registration points at the RETIRED console driver from an older
        // build. It must be removed, never trusted to fire.
        var runner = new ScriptedSchtasks(
            queryExit: 0,
            queryXml: TaskXml(@"C:\OldInstall\Mainguard.Installer.exe"),
            deleteExit: 0);
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.RebootPending, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.Deleted, result);
    }

    // ---- MG-9: the "is it ours" check compares canonical paths, and confines them to the install ----

    [Theory]
    // Same file, spelled differently. The old raw string compare called these FOREIGN and deleted a
    // legitimate registration — the harmless direction of a check that was wrong in both.
    [InlineData("C:/Apps/Mainguard/Mainguard.App.exe")]
    [InlineData(@"C:\APPS\MAINGUARD\MAINGUARD.APP.EXE")]
    public void Sweep_RebootPending_OwnTaskSpelledDifferently_IsStillRecognised(string registered)
    {
        var runner = new ScriptedSchtasks(queryExit: 0, queryXml: TaskXml(registered), deleteExit: 0);
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.RebootPending, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.KeptAwaitingReboot, result);
    }

    [Theory]
    // An elevated ONLOGON task pointing anywhere but this install must be deleted, never trusted to
    // fire. Each of these would have been KEPT — or was reachable — under a raw string compare of
    // paths that were never canonicalised or confined in the first place.
    [InlineData(@"C:\Users\victim\Downloads\evil.exe")]
    [InlineData(@"C:\Apps\Mainguard-evil\Mainguard.App.exe")]   // sibling sharing a name prefix
    [InlineData(@"C:\Apps\Mainguard\sub\..\..\evil.exe")]        // traversal out of the install
    [InlineData(@"\\attacker\share\Mainguard.App.exe")]          // off-machine
    [InlineData(@"C:\Apps\Mainguard\Mainguard.App.exe:evil.exe")] // NTFS alternate data stream
    // Traversal is REFUSED, not resolved — so even a registration that would resolve back to our own
    // exe is treated as foreign and deleted. That is the safe direction: the cost is one re-prompted
    // setup, whereas resolving '.' and '..' would mean accepting the very syntax used to walk out.
    [InlineData(@"C:\Apps\Mainguard\.\Mainguard.App.exe")]
    [InlineData(@"C:\Apps\Mainguard\sub\..\Mainguard.App.exe")]
    public void Sweep_RebootPending_RegistrationOutsideThisInstall_IsDeleted(string registered)
    {
        var runner = new ScriptedSchtasks(queryExit: 0, queryXml: TaskXml(registered), deleteExit: 0);
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.RebootPending, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.Deleted, result);
        Assert.Contains(runner.Invocations, args => args.Contains("/Delete"));
    }

    [Fact]
    public void Sweep_DeleteDenied_IsReportedNotSwallowed()
    {
        // Unelevated delete of an elevated task → ERROR_ACCESS_DENIED. The guard reports it (the
        // elevated resume launch self-deletes on its next fire) instead of pretending success.
        var runner = new ScriptedSchtasks(queryExit: 0, queryXml: TaskXml(CurrentExe), deleteExit: 1);
        var result = new ResumeTaskGuard(runner.Run).Sweep(OobeStage.Done, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.DeleteDenied, result);
    }

    [Fact]
    public void Sweep_SchtasksUnavailable_IsANoOp()
    {
        // Non-Windows / schtasks missing: the guard must never take the app down.
        var guard = new ResumeTaskGuard(_ => throw new InvalidOperationException("no schtasks here"));
        var result = guard.Sweep(OobeStage.Done, launchedByResumeTask: false, CurrentExe);

        Assert.Equal(ResumeTaskSweepResult.NotRegistered, result);
    }

    // ---- the pure XML command parser ----

    [Fact]
    public void ParseResumeTaskCommand_ExtractsAndUnquotesTheCommand()
    {
        var command = InstallerCommands.ParseResumeTaskCommand(TaskXml(CurrentExe));
        Assert.Equal(CurrentExe, command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<Task></Task>")]
    public void ParseResumeTaskCommand_MalformedXml_ReturnsNull(string xml)
    {
        Assert.Null(InstallerCommands.ParseResumeTaskCommand(xml));
    }

    // ---- the cross-process instance lock ----

    [Fact]
    public void OobeInstanceLock_SecondAcquire_FailsWhileHeld_ThenSucceedsAfterRelease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mainguard-test-oobe-{Guid.NewGuid():N}.lock");
        try
        {
            using (var first = OobeInstanceLock.TryAcquire(path))
            {
                Assert.NotNull(first);
                Assert.Null(OobeInstanceLock.TryAcquire(path));
            }

            using var reacquired = OobeInstanceLock.TryAcquire(path);
            Assert.NotNull(reacquired);
        }
        finally
        {
            try { File.Delete(path); } catch { /* DeleteOnClose usually already removed it */ }
        }
    }

    // ---- helpers ----

    private sealed class ScriptedSchtasks
    {
        private readonly int _queryExit;
        private readonly string _queryXml;
        private readonly int _deleteExit;

        public List<IReadOnlyList<string>> Invocations { get; } = new();

        public ScriptedSchtasks(int queryExit, string queryXml, int deleteExit = 0)
        {
            _queryExit = queryExit;
            _queryXml = queryXml;
            _deleteExit = deleteExit;
        }

        public (int, string) Run(IReadOnlyList<string> args)
        {
            Invocations.Add(args);
            return args.Contains("/Query") ? (_queryExit, _queryXml) : (_deleteExit, string.Empty);
        }
    }
}
