using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-47 #9 — the coordinator conversation bridge. Proves it is real (not a mock): a sent message is
/// recorded and, when a reply engine is wired, the coordinator's reply lands as a turn; the production
/// engine genuinely drives a <see cref="CoordinatorAgent"/> tool loop; and with no engine the message is
/// still recorded with an honest system turn rather than a fabricated reply.
/// </summary>
public sealed class CoordinatorConversationTests
{
    [Fact]
    public async Task SendAsync_DrivesRealCoordinatorAgent_AndProjectsReply()
    {
        // A real CoordinatorAgent over a scripted model + the real (capped) tool surface.
        var tools = new CoordinatorTools("coordinator-1", new AdmissionController(), new FakeWorkers());
        var agent = new CoordinatorAgent("coordinator-1", new ScriptedModel("Two independent tasks — on it."), tools);

        var conversation = new CoordinatorConversationService(new CoordinatorAgentReplyEngine(agent));
        var changes = 0;
        conversation.Changed += () => Interlocked.Increment(ref changes);

        await conversation.SendAsync("split the auth work from the search work");

        var turns = conversation.Snapshot();
        Assert.Equal(ConversationRole.Human, turns[0].Role);
        Assert.Contains("split the auth work", turns[0].Text);
        Assert.Equal(ConversationRole.Coordinator, turns[^1].Role);
        Assert.Equal("Two independent tasks — on it.", turns[^1].Text);
        Assert.True(changes >= 2, "Changed should fire for both the human turn and the reply turn");
    }

    [Fact]
    public async Task SendAsync_WithNoEngine_RecordsMessage_WithHonestSystemTurn()
    {
        var conversation = new CoordinatorConversationService(engine: null);

        await conversation.SendAsync("anything");

        var turns = conversation.Snapshot();
        Assert.Equal(ConversationRole.Human, turns[0].Role);
        Assert.Equal(ConversationRole.SystemLine, turns[1].Role);
        Assert.Contains("No coordinator model", turns[1].Text);
    }

    private sealed class ScriptedModel : ICoordinatorModel
    {
        private readonly string _reply;
        public ScriptedModel(string reply) => _reply = reply;

        public Task<CoordinatorModelTurn> NextAsync(IReadOnlyList<CoordinatorMessage> transcript, CancellationToken ct)
            => Task.FromResult(CoordinatorModelTurn.Say(_reply));
    }

    private sealed class FakeWorkers : IWorkerControl
    {
        public IReadOnlyList<string> ActiveWorkerIds => Array.Empty<string>();
        public string? WorkerStatus(string agentId) => null;
        public Task<string> SpawnWorkerAsync(string title, string taskPrompt, decimal budgetUsd, CancellationToken ct) =>
            Task.FromResult("w-1");
        public Task SendPromptAsync(string agentId, string prompt, CancellationToken ct) => Task.CompletedTask;
        public Task RequestVerificationAsync(string agentId, CancellationToken ct) => Task.CompletedTask;
    }
}
