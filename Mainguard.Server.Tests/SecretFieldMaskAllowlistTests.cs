using System;
using Google.Protobuf;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F56 — the allowlist itself, asserted directly on <see cref="SecretFieldMask.Summarize"/> rather
/// than through a live daemon. The interceptor test proves the wiring; this proves the rule, message
/// by message, for the payloads the audit named: ReadAudit's decrypted records, terminal scrollback,
/// merge diffs, verification logs, task prompts, chat text and plan text.
///
/// <para>The shape of every assertion is the same, because the guarantee is: the content is not in
/// the string, and the string still says enough to diagnose the call.</para>
/// </summary>
public sealed class SecretFieldMaskAllowlistTests
{
    [Fact]
    public void TaskPrompt_RendersItsLengthOnly_NeverItsText()
    {
        const string prompt = "rewrite the auth module using key sk-live-abcdef0123456789";
        var summary = SecretFieldMask.Summarize(new SpawnAgentRequest
        {
            RepoHandle = "handle-1",
            AgentKind = "claude-code",
            TaskPrompt = prompt,
        });

        Assert.DoesNotContain("sk-live", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("rewrite", summary, StringComparison.Ordinal);
        Assert.Contains($"task_prompt=<str:{prompt.Length}>", summary, StringComparison.Ordinal);
        // The op is still identifiable, with its opaque ids and its size.
        Assert.StartsWith("SpawnAgentRequest{size=", summary, StringComparison.Ordinal);
        Assert.Contains("repo_handle=handle-1", summary, StringComparison.Ordinal);
        Assert.Contains("agent_kind=claude-code", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredSecretField_IsNotRenderedAtAll_NotEvenAsALength()
    {
        var summary = SecretFieldMask.Summarize(new SpawnAgentRequest
        {
            RepoHandle = "handle-1",
            ModelApiKey = "sk-ant-SUPER-SECRET-0123456789",
        });

        Assert.DoesNotContain("SUPER-SECRET", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("model_api_key", summary, StringComparison.Ordinal);
        Assert.Contains("omitted=", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact leak the audit cited: <c>ReadAudit</c> returns up to 500 decrypted chain payloads,
    /// none of which was ever a <c>// SECRET</c> field, so all 500 used to be written into rpc.log.
    /// A repeated message field now renders its count and nothing else.
    /// </summary>
    [Fact]
    public void ReadAuditResponse_RendersRecordCount_NotPayloads()
    {
        var response = new ReadAuditResponse { Persistent = true };
        response.Records.Add(new AuditRecordEntry
        {
            Seq = 7,
            Type = "agent_spawned",
            PayloadJson = "{\"prompt\":\"AUDIT-PAYLOAD-DEADBEEF\"}",
            Hash = "abc",
        });

        var summary = SecretFieldMask.Summarize(response);

        Assert.DoesNotContain("AUDIT-PAYLOAD-DEADBEEF", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_spawned", summary, StringComparison.Ordinal);
        Assert.Contains("records=[1]", summary, StringComparison.Ordinal);
        Assert.Contains("persistent=True", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedMessages_RecurseUnderTheSameRules()
    {
        var request = new ResumeAgentRequest { AgentId = "agent-9", AgentKind = "codex" };
        request.ExtraEnv.Add(new EnvEntry { Name = "LLM_TOKEN", Value = "ENV-SECRET-DEADBEEF" });

        var summary = SecretFieldMask.Summarize(request);

        Assert.DoesNotContain("ENV-SECRET", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("LLM_TOKEN", summary, StringComparison.Ordinal);
        Assert.Contains("extra_env=[1]", summary, StringComparison.Ordinal);
        Assert.Contains("agent_id=agent-9", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedScalars_AreRendered_SoAnAccessLogIsStillWorthReading()
    {
        var summary = SecretFieldMask.Summarize(new ReadAuditRequest { FromSeq = 41, Take = 200 });

        Assert.Contains("from_seq=41", summary, StringComparison.Ordinal);
        Assert.Contains("take=200", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// An allowlisted NAME is not a licence to print whatever is in the field. The value has to look
    /// like a handle too — short and identifier-shaped — otherwise a caller that stuffs a sentence
    /// into <c>agent_id</c> reopens the hole the allowlist just closed.
    /// </summary>
    [Fact]
    public void AllowlistedName_WithAnUnhandleLikeValue_StillRendersALengthOnly()
    {
        var longValue = new string('x', 100);
        var spaced = SecretFieldMask.Summarize(new ResumeAgentRequest { AgentId = "hello world secret" });
        var oversized = SecretFieldMask.Summarize(new ResumeAgentRequest { AgentId = longValue });

        Assert.Contains("agent_id=<str:18>", spaced, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", spaced, StringComparison.Ordinal);
        Assert.Contains("agent_id=<str:100>", oversized, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesFields_RenderTheirLength_NeverTheirContent()
    {
        var request = new ResumeAgentRequest { AgentId = "agent-1" };
        request.CliCredentials.Add(new CliCredentialFile
        {
            Path = "/home/u/.claude/.credentials.json",
            Content = ByteString.CopyFromUtf8("LOGIN-STATE-DEADBEEF"),
        });

        var summary = SecretFieldMask.Summarize(request);

        Assert.DoesNotContain("LOGIN-STATE", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(".credentials.json", summary, StringComparison.Ordinal);
        Assert.Contains("cli_credentials=[1]", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registry is still enforced (the <c>// SECRET</c> proto grep has a home), but it is no
    /// longer what stands between a new field and the log file.
    /// </summary>
    [Fact]
    public void SecretRegistry_StillAnswersForItsRegisteredFields()
    {
        Assert.True(SecretFieldMask.IsSecret("mainguard.v1.SpawnAgentRequest", 4));
        Assert.False(SecretFieldMask.IsSecret("mainguard.v1.SpawnAgentRequest", 2));
    }
}
