using System;
using System.Linq;
using Mainguard.Agents.Agents.Ipc;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR3 — the coordinator→daemon spawn channel's pure pieces: the newline-delimited JSON codec and
/// the <c>mainguard-agent</c> shim script the daemon writes into the coordinator's read-only IPC dir.
/// </summary>
public class AgentIpcProtocolTests
{
    [Fact]
    public void Request_RoundTrips()
    {
        var line = AgentIpcProtocol.SerializeRequest(new AgentIpcRequest("spawn", "claude-code", "split the work"));
        Assert.DoesNotContain('\n', line);

        var parsed = AgentIpcProtocol.TryParseRequest(line);
        Assert.NotNull(parsed);
        Assert.Equal("spawn", parsed!.Op);
        Assert.Equal("claude-code", parsed.AgentKind);
        Assert.Equal("split the work", parsed.TaskPrompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"agentKind\":\"x\"}")] // no op
    [InlineData("{\"op\":\"\"}")]         // empty op
    [InlineData("[1,2,3]")]
    public void MalformedRequest_ParsesToNull_NeverThrows(string line)
    {
        Assert.Null(AgentIpcProtocol.TryParseRequest(line));
    }

    [Fact]
    public void Response_SerializesAsOneLine_OmittingNulls()
    {
        var ok = AgentIpcProtocol.SerializeResponse(new AgentIpcResponse(Ok: true, AgentId: "a1"));
        Assert.DoesNotContain('\n', ok);
        Assert.Contains("\"agentId\":\"a1\"", ok, StringComparison.Ordinal);
        Assert.DoesNotContain("error", ok, StringComparison.Ordinal);

        var error = AgentIpcProtocol.SerializeResponse(new AgentIpcResponse(Ok: false, Error: "refused"));
        Assert.Contains("\"ok\":false", error, StringComparison.Ordinal);
        Assert.Contains("refused", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ShimScript_SpeaksTheProtocol_AtTheFixedMountPath()
    {
        // The script is python3 (part of the pre-baked jail toolchain — G-16: nothing is baked into
        // the image), dials the fixed in-jail socket path by default, and emits the contract's ops.
        Assert.StartsWith("#!/usr/bin/env python3", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains(AgentIpcPaths.SandboxSocketPath, AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("MAINGUARD_IPC_SOCKET", AgentSpawnShim.Script, StringComparison.Ordinal);

        // Phase 3 — the shim emits the coordinator contract §3 surface. `list` is still accepted on the
        // command line as an alias of `status` (an existing coordinator transcript keeps working), but it
        // is no longer an op ON THE WIRE: both spellings send `status`, so the daemon has one code path
        // to scope and one to test.
        Assert.Contains("\"op\": \"spawn\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"status\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"prompt\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"verify\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("(\"status\", \"list\")", AgentSpawnShim.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every op the DAEMON serves a worker has a way for the worker to reach it. An exhaustiveness check
    /// over <see cref="AgentIpcRequest.WorkerOps"/> rather than one assertion per op, because the failure
    /// it guards against is the one this branch keeps finding: a complete handler with nothing able to
    /// call it. <c>commit_work</c> is the newest instance — a daemon that can commit for a worker is worth
    /// nothing if the worker's only executable has no subcommand for it.
    /// </summary>
    [Fact]
    public void TheWorkerShim_CanReachEveryOpTheDaemonServesAWorker()
    {
        foreach (var op in AgentIpcRequest.WorkerOps)
        {
            Assert.Contains($"\"op\": \"{op}\"", WorkerPlanShim.Script, StringComparison.Ordinal);
        }

        // …and the command line a worker actually types for it — for EVERY op, from the verb map that is
        // supposed to be the single source of those spellings.
        foreach (var op in AgentIpcRequest.WorkerOps)
        {
            Assert.Contains(
                $"argv[1] == \"{WorkerPlanShim.Verbs[op]}\"", WorkerPlanShim.Script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>The verb map is the single source, and only a source scan can say so.</b>
    ///
    /// <para><see cref="WorkerPlanShim.Verbs"/> exists so the two spellings of one operation — the verb a
    /// worker types and the wire op the daemon dispatches — are written down together once. Most of the
    /// generated shim honoured that; <c>brief</c>, <c>present</c>, <c>await</c> and <c>commit</c> were
    /// still hardcoded literals in both their <c>argv[1] ==</c> comparison and their <c>{"op": …}</c>
    /// payload. Nothing failed, because the literals happened to equal the map — which is exactly the
    /// state a drift starts from: change one entry in the map and the shim keeps sending the old spelling,
    /// silently, to a daemon that no longer serves it.</para>
    ///
    /// <para>No runtime assertion can catch that. <see cref="WorkerPlanShim.Script"/> is composed once at
    /// type-load from the map, so a test can only ever compare the map with a string built from the map —
    /// which passes either way. The property is about the SOURCE ("no dispatch spelling is written twice"),
    /// so the source is what is read. Anchored so it cannot pass vacuously: it asserts it found the right
    /// file, that the interpolated forms are there in the numbers the op table says, and — the real
    /// control — that the same matchers DO fire on a bare literal.</para>
    /// </summary>
    [Fact]
    public void TheWorkerShimsDispatch_IsGeneratedFromTheVerbMap_NeverWrittenOutAsALiteral()
    {
        var source = ShimSource();

        // Anchors, before the assertion that matters: a scan pointed at the wrong file reports a clean
        // bill of health for a file it never opened.
        Assert.Contains("public static class WorkerPlanShim", source, StringComparison.Ordinal);
        Assert.Contains("def main(argv):", source, StringComparison.Ordinal);

        // The control: these matchers can see a bare literal. If they could not, the two emptiness
        // assertions below would measure nothing at all.
        Assert.Matches(BareVerbLiteral, "    if len(argv) >= 2 and argv[1] == \"brief\":");
        Assert.Matches(BareOpLiteral, "        request = {\"op\": \"brief\"}");

        Assert.Empty(BareVerbLiteral.Matches(source).Select(m => m.Value));
        Assert.Empty(BareOpLiteral.Matches(source).Select(m => m.Value));

        // …and the dispatch really is there: one interpolated verb comparison and one interpolated wire op
        // per op the daemon serves a worker. Stated as counts so deleting a branch fails here too.
        Assert.Equal(AgentIpcRequest.WorkerOps.Count, InterpolatedVerb.Matches(source).Count);
        Assert.Equal(AgentIpcRequest.WorkerOps.Count, InterpolatedOp.Matches(source).Count);
    }

    /// <summary>A verb spelled out in the shim's dispatch instead of taken from the map.</summary>
    private static readonly System.Text.RegularExpressions.Regex BareVerbLiteral =
        new(@"argv\[1\] == ""[a-z_]+""", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A wire op spelled out in a request payload instead of taken from the constants.</summary>
    private static readonly System.Text.RegularExpressions.Regex BareOpLiteral =
        new(@"\{""op"": ""[a-z_]+""", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex InterpolatedVerb =
        new(@"argv\[1\] == ""\{\{Verbs\[AgentIpcRequest\.\w+\]\}\}""",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex InterpolatedOp =
        new(@"\{""op"": ""\{\{AgentIpcRequest\.\w+\}\}""",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The shim's own source file. Never falls back to the test binary's directory: a scan that
    /// found nothing would report success for a file it never read.</summary>
    private static string ShimSource()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Mainguard.slnx")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        var path = dir is null
            ? null
            : System.IO.Path.Combine(dir, "Mainguard.Agents", "Agents", "Ipc", "WorkerPlanShim.cs");

        if (path is null || !System.IO.File.Exists(path))
        {
            throw new System.IO.FileNotFoundException(
                $"WorkerPlanShim.cs could not be located above '{AppContext.BaseDirectory}', so the "
                + "single-sourcing of the shim's verbs could not be checked. This is a failure, not a skip.");
        }

        return System.IO.File.ReadAllText(path);
    }

    /// <summary>
    /// Both shims are Python living inside a C# string literal, so nothing in the C# build has an opinion
    /// about whether they parse. A syntax error would ship to every jail and surface as a worker that
    /// cannot reach the daemon at all — indistinguishable, from the daemon's side, from a worker that
    /// simply never called. Compiled with the real interpreter; skipped where there is none, because a
    /// missing python3 on a dev box is not evidence about the script.
    /// </summary>
    [Fact]
    public void BothShims_AreValidPython()
    {
        foreach (var (name, source) in new[]
                 {
                     ("mainguard-agent", AgentSpawnShim.Script),
                     ("mainguard-plan", WorkerPlanShim.Script),
                 })
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"mg-shim-{Guid.NewGuid():N}-{name}.py");
            System.IO.File.WriteAllText(path, source);
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("python3")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add("py_compile");
                start.ArgumentList.Add(path);

                using var process = System.Diagnostics.Process.Start(start);
                if (process is null)
                {
                    return; // no python3 on this box — nothing measured, nothing claimed
                }

                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"{name} is not valid python: {stderr}");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // python3 is not installed here
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// <b>The spawn parser, run.</b> The contract §3 change of 2026-08-29 put a required
    /// <c>--title</c> / <c>--task</c> pair on <c>mainguard-agent spawn</c>, and the whole reason for that
    /// spelling is that a quoting slip must be <i>detected</i> rather than silently mis-split. That is a
    /// property of Python living inside a C# string literal, which no assertion about the script's TEXT
    /// can check — so this runs the shim's own <c>main()</c> under the real interpreter with the
    /// transport stubbed, which also proves a refused spawn never reaches the daemon.
    ///
    /// <para>The real-jail leg of the same claim is <c>AgentIpcJailDockerTests.TheRealShimsSpawn_*</c>;
    /// this one is in the everyday tier so a regression is caught on a box with no Docker.</para>
    /// </summary>
    [Theory]
    // The taught form: an unquoted, multi-word task tail is fine — it is the argument that is hard to
    // quote, so it is the one that does not have to be.
    [InlineData(
        new[] { "spawn", "claude-code", "--title", "Fix the token clock", "--task", "rewrite", "TokenClock", "in", "UTC" },
        "Fix the token clock", "rewrite TokenClock in UTC", null)]
    // The pre-change invocation. Refused — not turned into a title by deriving one from the task, which
    // is the fallback this change exists to remove.
    [InlineData(new[] { "spawn", "claude-code", "rewrite", "TokenClock" }, null, null, "--title")]
    // An UNQUOTED title. The stray words land where --task must be, so the slip is diagnosed instead of
    // producing a one-word title and a truncated task. This case is the reason for two named flags.
    [InlineData(
        new[] { "spawn", "claude-code", "--title", "Fix", "the", "clock", "--task", "rewrite" },
        null, null, "ONE quoted argument")]
    [InlineData(new[] { "spawn", "claude-code", "--title", "Fix the clock" }, null, null, "--task")]
    [InlineData(new[] { "spawn", "claude-code", "--title", "Fix the clock", "--task" }, null, null, "the task text")]
    [InlineData(new[] { "spawn", "--title", "Fix the clock", "--task", "work" }, null, null, "agent kind")]
    public void TheShimsSpawnParser_AcceptsTheTaughtForm_AndDiagnosesEverySlip(
        string[] args, string? expectedTitle, string? expectedTask, string? expectedRefusal)
    {
        var parsed = RunSpawnParser(args);
        if (parsed is null)
        {
            return; // no python3 on this box — nothing measured, nothing claimed
        }

        var (title, task, refusal, reported) = parsed.Value;
        if (expectedRefusal is null)
        {
            Assert.Null(refusal);
            Assert.Equal(expectedTitle, title);
            Assert.Equal(expectedTask, task);
            Assert.NotEqual(title, task);
        }
        else
        {
            Assert.Null(title);
            Assert.Contains(expectedRefusal, refusal!, StringComparison.Ordinal);

            // Every refusal shows the form that works, with the shim's own path in it — a coordinator
            // reads this and retries; a refusal that only says "no" costs it a turn to rediscover.
            Assert.Contains(AgentSpawnShim.SpawnUsage, refusal!, StringComparison.Ordinal);

            // <b>G3.</b> A refused spawn is REPORTED, so it exists somewhere other than this jail's
            // stderr — two of three first spawns in a stress run exited 2 with zero daemon log lines.
            Assert.NotNull(reported);

            // …and reporting is not guessing. The attempt carries no task, so the daemon's channel
            // check refuses it before anything is minted; a report that could be served would be a
            // worse defect than the invisibility it fixes.
            Assert.DoesNotContain("\"taskPrompt\"", reported!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>Defect G3, both halves, measured through a real shell.</b> The taught form is what a
    /// coordinator types into its CLI's Bash tool, so the claim "this form works" is a claim about what
    /// <c>bash -c</c> does with it — not about what the parser would do given clean argv.
    ///
    /// <para>The instructions used to say <c>--task</c> "needs no quotes at all", reasoning that the
    /// parser joins every remaining word. It does. But a task describing code contains <c>()</c>, and
    /// <c>bash -c</c> never reaches the parser: <c>syntax error near unexpected token '('</c>, exit 2,
    /// and — because nothing ran — nothing in the daemon's log either.</para>
    /// </summary>
    [Fact]
    public void TheTaughtSpawnForm_SurvivesAShell_AndTheOldUnquotedOneDoesNot()
    {
        const string Task = "rewrite add() and multiply() so they reject non-numbers";

        var quoted = RunSpawnParserThroughBash($"spawn claude-code --title 'Validate inputs' --task \"{Task}\"");
        if (quoted is null)
        {
            return; // no python3/bash on this box — nothing measured, nothing claimed
        }

        Assert.Null(quoted.Value.Refusal);
        Assert.Equal("Validate inputs", quoted.Value.Title);
        Assert.Equal(Task, quoted.Value.Task);

        // The advice that was there before, run: the shell kills the line and the shim never starts.
        var unquoted = RunSpawnParserThroughBash($"spawn claude-code --title 'Validate inputs' --task {Task}");
        Assert.NotNull(unquoted);
        Assert.Null(unquoted!.Value.Title);
        Assert.Null(unquoted.Value.Task);
        Assert.Null(unquoted.Value.Reported);   // nothing ran, so nothing could be reported
        Assert.Contains("syntax error", unquoted.Value.Refusal ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>What one driven run of the shim's <c>main()</c> produced.</summary>
    /// <param name="Reported">The JSON of the request that reached the stubbed transport, or null when
    /// none did. On a refusal this is the G3 report — the attempt the shim could not build, sent so the
    /// daemon can refuse it out loud rather than the failure existing only inside the jail.</param>
    private readonly record struct DrivenSpawn(
        string? Title, string? Task, string? Refusal, string? Reported);

    /// <summary>
    /// Runs the taught command line through <c>bash -c</c> and then the shim's own <c>main()</c>, so the
    /// shell is part of what is measured. Null where python3 or bash is unavailable.
    /// </summary>
    private static DrivenSpawn? RunSpawnParserThroughBash(string commandLine)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mg-shim-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var (shim, driver) = WriteDriver(dir);
            var start = new System.Diagnostics.ProcessStartInfo("bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"python3 {Quote(driver)} {Quote(shim)} {commandLine}");

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // The shell refused to parse the line: the shim never started, so there is no request, no
            // report, and nothing on stdout. This is the failure the old advice produced.
            if (process.ExitCode != 0 && stdout.Trim().Length == 0)
            {
                return new DrivenSpawn(null, null, stderr, null);
            }

            return Read(stdout);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no bash or no python3 here
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    /// <summary>Runs the shim's own <c>main()</c> under python3, or null where there is none.</summary>
    private static DrivenSpawn? RunSpawnParser(string[] args)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mg-shim-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var (shim, driver) = WriteDriver(dir);
            var start = new System.Diagnostics.ProcessStartInfo("python3")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add(driver);
            start.ArgumentList.Add(shim);
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"the shim's spawn parser threw: {stderr}");
            return Read(stdout);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // python3 is not installed here
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Writes the real shim plus a driver that runs its OWN <c>main()</c> with the transport stubbed —
    /// not <c>spawn_request</c> in isolation. A correct parser that <c>main()</c> does not route through
    /// is exactly the shape of the defect the 2026-08-29 change fixed, so the wiring is part of what is
    /// measured. The stub also captures what — if anything — reached the daemon.
    /// </summary>
    private static (string Shim, string Driver) WriteDriver(string dir)
    {
        var shim = System.IO.Path.Combine(dir, "mainguard_agent_shim.py");
        var driver = System.IO.Path.Combine(dir, "driver.py");
        System.IO.File.WriteAllText(shim, AgentSpawnShim.Script);
        System.IO.File.WriteAllText(driver, """
            import contextlib, importlib.util, io, json, sys
            spec = importlib.util.spec_from_file_location("shim", sys.argv[1])
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            seen = {}
            def fake_call(request, timeout=60):
                seen["request"] = request
                return {"ok": True, "agentId": "w-1"}
            mod.call = fake_call
            err = io.StringIO()
            with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
                code = mod.main(["/opt/mainguard/ipc/mainguard-agent"] + sys.argv[2:])
            print(json.dumps({"exit": code, "request": seen.get("request"), "stderr": err.getvalue()}))
            """);
        return (shim, driver);
    }

    /// <summary>Reads the driver's one JSON line into a <see cref="DrivenSpawn"/>.</summary>
    private static DrivenSpawn Read(string stdout)
    {
        using var document = System.Text.Json.JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var reached = root.GetProperty("request");
        var reported = reached.ValueKind == System.Text.Json.JsonValueKind.Null
            ? null
            : reached.GetRawText();

        // A refusal is spelled by the exit code, not by "nothing reached the transport" — since G3 a
        // refused spawn DOES reach it, as a report the daemon then refuses out loud.
        if (root.GetProperty("exit").GetInt32() != 0)
        {
            return new DrivenSpawn(null, null, root.GetProperty("stderr").GetString(), reported);
        }

        Assert.NotNull(reported);
        return new DrivenSpawn(
            reached.TryGetProperty("title", out var t) ? t.GetString() : null,
            reached.GetProperty("taskPrompt").GetString(),
            null,
            reported);
    }

    /// <summary>
    /// <b>Defect G4, the shim's half.</b> <c>commit</c> takes ONE quoted argument. It used to be
    /// <c>' '.join(argv[2:])</c>, which rejoins with single spaces whatever the shell handed it — so a
    /// worker's subject / blank line / body arrived as one flat line and nothing anywhere could tell
    /// that a structure had been lost. A second positional is now refused, with the reason.
    ///
    /// <para>Run through the real <c>main()</c> under python3, because the property being asserted is
    /// that the DISPATCH refuses, not that some helper would have.</para>
    /// </summary>
    [Theory]
    // The taught form: one argument, newlines and all, passed through untouched.
    [InlineData(new[] { "commit", "feat: a subject\n\nA body paragraph." }, "feat: a subject\n\nA body paragraph.", null)]
    // An empty message is still allowed — the daemon defaults it, and refusing would lose the work.
    [InlineData(new[] { "commit" }, "", null)]
    // The slip: a shell-split message. Refused rather than rejoined into a single line.
    [InlineData(new[] { "commit", "feat:", "a", "subject" }, null, "ONE quoted argument")]
    public void TheShimsCommit_TakesOneQuotedMessage_AndRefusesAShellSplitOne(
        string[] args, string? expectedMessage, string? expectedRefusal)
    {
        var run = RunPlanShim(args);
        if (run is null)
        {
            return; // no python3 on this box — nothing measured, nothing claimed
        }

        var (message, refusal) = run.Value;
        if (expectedRefusal is null)
        {
            Assert.Null(refusal);
            Assert.Equal(expectedMessage, message);
        }
        else
        {
            Assert.Null(message);
            Assert.Contains(expectedRefusal, refusal!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>The re-scope verb, through the real dispatch.</b> Three properties, and only the first is about
    /// the happy path:
    ///
    /// <list type="number">
    /// <item>the verb builds a <c>rescope_plan</c> request carrying the plan id AND the plan document — an
    /// id-less re-scope would leave the daemon guessing which approval was being widened;</item>
    /// <item>a bare <c>rescope</c> is refused BEFORE any round trip, and the refusal prints the form. The
    /// daemon refuses it too, and that is the enforcement; this is the affordance that costs no turn;</item>
    /// <item>the refusal sends NOTHING — asserted as a null request, because "refused" and "sent something
    /// the daemon then refused" are different facts and only the second leaves a card behind.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(new[] { "rescope", "plan-7", "PLAN_FILE" }, null)]
    [InlineData(new[] { "rescope" }, "rescope <approved-plan-id> <plan.json>")]
    [InlineData(new[] { "rescope", "plan-7" }, "rescope <approved-plan-id> <plan.json>")]
    public void TheShimsRescope_NamesThePlanItWidens_AndRefusesLocallyWhenItCannot(
        string[] args, string? expectedRefusal)
    {
        var run = RunPlanShimRequest(args);
        if (run is null)
        {
            return; // no python3 on this box — nothing measured, nothing claimed
        }

        var (requestJson, refusal) = run.Value;
        if (expectedRefusal is not null)
        {
            Assert.Null(requestJson);
            Assert.Contains(expectedRefusal, refusal!, StringComparison.Ordinal);
            return;
        }

        Assert.Null(refusal);
        using var request = System.Text.Json.JsonDocument.Parse(requestJson!);
        Assert.Equal(AgentIpcRequest.RescopePlanOp, request.RootElement.GetProperty("op").GetString());
        Assert.Equal("plan-7", request.RootElement.GetProperty("planId").GetString());
        Assert.Contains(
            "src/a.cs", request.RootElement.GetProperty("planJson").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The re-scope refusal points at <c>brief</c>, so <c>brief</c> has to actually show the id.</b>
    ///
    /// <para>This is defect G3's lesson applied to a sentence written in this change. The id-less
    /// <c>rescope</c> refusal ends "(`mainguard-plan brief` prints the id of your live plan.)" — and until
    /// the line under test existed that was FALSE: the daemon put <c>planId</c> and <c>status</c> on the
    /// brief response and the shim printed only the brief text, so a worker following the advice got a
    /// headline and no id, and had no way to run the command it had just been told to run. G3 was exactly
    /// this shape — advice that was true of the parser and false of the world — and it cost two of three
    /// spawns in a stress run.</para>
    ///
    /// <para>Both directions, because either alone passes on the defect: the refusal must name
    /// <c>brief</c>, and <c>brief</c> must print the id.</para>
    /// </summary>
    [Fact]
    public void TheShimsBrief_PrintsTheLivePlanId_BecauseTheRescopeRefusalSendsTheWorkerThere()
    {
        var refusal = RunPlanShimIo(new[] { "rescope" });
        var brief = RunPlanShimIo(new[] { "brief" });
        if (refusal is null || brief is null)
        {
            return; // no python3 on this box — nothing measured, nothing claimed
        }

        Assert.Contains("brief", refusal.Value.Refusal!, StringComparison.Ordinal);

        // …and what that command prints: the headline AND the id the refusal promised, with its state,
        // because "which plan" and "is it approved yet" are the two facts a re-scope needs.
        Assert.Contains("Fix the token clock", brief.Value.Stdout, StringComparison.Ordinal);
        Assert.Contains("p1", brief.Value.Stdout, StringComparison.Ordinal);
        Assert.Contains("Approved", brief.Value.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Runs <c>mainguard-plan</c>'s own <c>main()</c> with the transport stubbed, and answers
    /// with the ONE thing this shim's commit path contributes: the message.</summary>
    private static (string? Message, string? Refusal)? RunPlanShim(string[] args)
    {
        var run = RunPlanShimRequest(args);
        if (run is null)
        {
            return null;
        }

        var (requestJson, refusal) = run.Value;
        if (requestJson is null)
        {
            return (null, refusal);
        }

        using var request = System.Text.Json.JsonDocument.Parse(requestJson);
        return (request.RootElement.GetProperty("message").GetString(), null);
    }

    /// <summary>
    /// Runs <c>mainguard-plan</c>'s own <c>main()</c> with the transport stubbed and returns the REQUEST
    /// it would have sent, verbatim, or the refusal that stopped it.
    ///
    /// <para>Through <c>main()</c> rather than a helper, for the reason §13.6 wrote down: M9 changed only
    /// the spawn shim's <c>main</c> while its parser stayed correct, and a test that called the parser
    /// directly stayed green while the shim on disk sent the wrong thing. A correct parser the dispatch
    /// does not route through is exactly the shape of the defect.</para>
    /// </summary>
    private static (string? RequestJson, string? Refusal)? RunPlanShimRequest(
        string[] args, string? planFileContents = null)
    {
        var run = RunPlanShimIo(args, planFileContents);
        return run is null ? null : (run.Value.RequestJson, run.Value.Refusal);
    }

    /// <summary>The same run, with the shim's own STDOUT — what the worker actually reads.</summary>
    private static (string? RequestJson, string? Refusal, string Stdout)? RunPlanShimIo(
        string[] args, string? planFileContents = null)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mg-plan-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var shim = System.IO.Path.Combine(dir, "mainguard_plan_shim.py");
            var driver = System.IO.Path.Combine(dir, "driver.py");
            System.IO.File.WriteAllText(shim, WorkerPlanShim.Script);
            System.IO.File.WriteAllText(driver, PlanDriverSource);

            var start = new System.Diagnostics.ProcessStartInfo("python3")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            // `present`, `revise` and `rescope` read a plan document off disk before they build a
            // request, so a test of those verbs has to give the real main() a real file. PLAN_FILE is
            // substituted rather than hardcoded because the path is per-run.
            var planFile = System.IO.Path.Combine(dir, "plan.json");
            System.IO.File.WriteAllText(
                planFile,
                planFileContents ?? "{\"scope\":[\"src/a.cs\"],\"approach\":\"a\",\"testStrategy\":\"t\"}");

            start.ArgumentList.Add(driver);
            start.ArgumentList.Add(shim);
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg == "PLAN_FILE" ? planFile : arg);
            }

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            using var document = System.Text.Json.JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var stdout_ = root.GetProperty("stdout").GetString() ?? "";
            if (root.GetProperty("exit").GetInt32() != 0)
            {
                return (null, root.GetProperty("stderr").GetString(), stdout_);
            }

            var request = root.GetProperty("request");
            return (
                request.ValueKind == System.Text.Json.JsonValueKind.Null ? null : request.GetRawText(),
                null,
                stdout_);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // python3 is not installed here
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string PlanDriverSource =
        "import contextlib, importlib.util, io, json, sys\n"
        + "spec = importlib.util.spec_from_file_location(\"shim\", sys.argv[1])\n"
        + "mod = importlib.util.module_from_spec(spec)\n"
        + "spec.loader.exec_module(mod)\n"
        + "seen = {}\n"
        + "def fake_call(request, timeout=None):\n"
        + "    seen[\"request\"] = request\n"
        + "    if request.get(\"op\") == \"brief\":\n"
        // Shaped exactly as AgentSpawnService.Brief answers: the brief, plus the live plan's id and
        // state. The shim's own output is what this stubs FOR — see TheShimsBrief_PrintsTheLivePlanId.
        + "        return {\"ok\": True, \"brief\": \"Fix the token clock\", \"planId\": \"p1\",\n"
        + "                \"status\": \"Approved\", \"revision\": 0, \"maxRevisions\": 3}\n"
        + "    if request.get(\"op\") in (\"present_plan\", \"revise_plan\", \"rescope_plan\", \"await_decision\"):\n"
        + "        return {\"ok\": True, \"status\": \"Approved\", \"planId\": \"p1\", \"taskPrompt\": \"t\"}\n"
        + "    return {\"ok\": True, \"committed\": True, \"commitSha\": \"abc\", \"status\": \"agent/x\"}\n"
        + "mod.call = fake_call\n"
        + "err = io.StringIO()\n"
        + "out = io.StringIO()\n"
        + "with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):\n"
        + "    code = mod.main([\"/opt/mainguard/ipc/mainguard-plan\"] + sys.argv[2:])\n"
        + "print(json.dumps({\"exit\": code, \"request\": seen.get(\"request\"),\n"
        + "                  \"stdout\": out.getvalue(), \"stderr\": err.getvalue()}))\n";

    [Fact]
    public void IpcPaths_AreTheFixedInJailLayout()
    {
        Assert.Equal("/opt/mainguard/ipc", AgentIpcPaths.SandboxMount);
        Assert.Equal("/opt/mainguard/ipc/daemon.sock", AgentIpcPaths.SandboxSocketPath);
        Assert.Equal("mainguard-agent", AgentIpcPaths.ShimFileName);

        // The outbox is a fixed child of the IPC mount, not a target of its own: the read-only mount
        // keeps covering the shim and the instructions while the mailbox nests inside it.
        Assert.Equal("/opt/mainguard/ipc/outbox", AgentIpcPaths.SandboxOutboxPath);
        Assert.Equal(AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.OutboxDirName, AgentIpcPaths.SandboxOutboxPath);
    }

    /// <summary>
    /// Both shims speak the channel through ONE transport. They each carried their own copy of
    /// <c>call()</c> before, and the copies had already drifted (one took a timeout, the other did not);
    /// a jail's only route to the daemon is not a place where two implementations may disagree — least of
    /// all now that choosing between two transports lives inside it.
    /// </summary>
    [Fact]
    public void BothShims_EmbedTheOneSharedTransport()
    {
        Assert.Contains(AgentIpcShimTransport.PythonSource, AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains(AgentIpcShimTransport.PythonSource, WorkerPlanShim.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The socket is the channel; the outbox is what the channel becomes where a bind-mounted socket is
    /// inert (macOS — the daemon is on the host, the jail is in the engine's Linux VM, and virtiofs does
    /// not proxy AF_UNIX). The fallback is gated on the outbox being WRITABLE, and that gate is
    /// load-bearing: where the socket is real the outbox sits inside the read-only mount, so a daemon
    /// that is genuinely down still reports as a daemon that is down instead of parking the CLI on a poll
    /// loop until its deadline.
    /// </summary>
    [Fact]
    public void TheShimTransport_FallsBackToTheOutbox_OnlyWhenTheOutboxIsWritable()
    {
        var source = AgentIpcShimTransport.PythonSource;

        Assert.Contains(AgentIpcPaths.SandboxOutboxPath, source, StringComparison.Ordinal);
        Assert.Contains("MAINGUARD_IPC_OUTBOX", source, StringComparison.Ordinal);
        Assert.Contains("os.access(OUTBOX_PATH, os.W_OK)", source, StringComparison.Ordinal);

        // The socket is tried FIRST — the outbox is the exception, not the default.
        Assert.True(
            source.IndexOf("_call_socket(request, timeout)", StringComparison.Ordinal)
            < source.IndexOf("_call_outbox(request, timeout)", StringComparison.Ordinal),
            "the shim reaches for the outbox before it has tried the socket");

        // Staged then renamed, so the daemon can never claim half a request.
        Assert.Contains("os.rename(staged, request_path)", source, StringComparison.Ordinal);
    }
}
