using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// An egress REFUSAL must reach the daemon log at <see cref="LogLevel.Warning"/> — the level an
/// operator actually filters on when they want to know what their agents are being stopped from
/// reaching.
///
/// <para><b>The defect.</b> <see cref="LoggingTransparencyLog"/> chose the level with
/// <c>string.Equals(line.Verdict, "Denied", OrdinalIgnoreCase)</c> against a free-form string field.
/// The only producer in the daemon, <see cref="DaemonGitProxy"/>, writes <c>"refused"</c> and
/// <c>"allowed"</c>. The comparison was therefore <b>always false</b>: every refusal was logged at
/// Information, so an operator watching at Warning saw complete silence while a jailed agent probed
/// blocked hosts. Nothing was broken on either side alone — nothing made the two agree.</para>
///
/// <para><b>Why these tests are shaped this way.</b> They never name a verdict value. The line is
/// produced by driving the REAL producer through the REAL sink, and the assertion is on the log level
/// the pair arrives at. That is deliberate: a test that constructed a <c>TransparencyLine</c> with a
/// verdict of its own choosing would be asserting the sink's behaviour given the spelling the TEST
/// picked, which is precisely the blind spot that let the two drift apart. It also means this file is
/// byte-identical before and after the fix — it fails against the string comparison and passes against
/// the typed <see cref="EgressVerdict"/>, with no test-side change to explain the difference away.</para>
/// </summary>
public sealed class EgressRefusalLogLevelTests
{
    /// <summary>The one allowlisted prefix — everything else is a refusal (A6).</summary>
    private static readonly GitProxyPrefix Allowed = new("github.com", "myorg");

    [Fact]
    public void RefusedFetch_ReachesTheDaemonLogAtWarning_NotInformation()
    {
        var logs = new CapturingProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var proxy = ProxyLoggingTo(factory);

        // A prefix that is not allowlisted: refused, audited, and transparency-logged.
        Assert.Throws<GitProxyRefusedException>(() => proxy.ForwardService(new GitProxyRequest(
            DaemonGitProxy.GitUploadPack, "github.com", "attacker", "payload", "agent-1")));

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(DaemonLogCategories.Egress, entry.Category);

        // The line names WHICH agent and WHICH host, so the Warning is actionable rather than bare.
        // Asserted on the summary fields the schema promises (host/kind/agent/bytes) — deliberately not
        // on the verdict's spelling, which is the coupling this whole defect came from.
        Assert.Contains("host=github.com", entry.Message, StringComparison.Ordinal);
        Assert.Contains("agent=agent-1", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control, and it is load-bearing: without it, a sink that logged EVERYTHING at
    /// Warning would satisfy the test above while destroying the level's meaning — an operator filtering
    /// at Warning would then see every ordinary fetch and learn nothing from either.
    /// </summary>
    [Fact]
    public void AllowedFetch_StaysAtInformation_SoWarningKeepsMeaningRefusal()
    {
        var logs = new CapturingProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var proxy = ProxyLoggingTo(factory);

        proxy.ForwardService(new GitProxyRequest(
            DaemonGitProxy.GitUploadPack, Allowed.Host, Allowed.Org, "lib", "agent-1"));

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    /// <summary>The production pair: the real git proxy recording into the real logging sink.</summary>
    private static DaemonGitProxy ProxyLoggingTo(ILoggerFactory factory) => new(
        new[] { Allowed },
        new InMemoryAuditLog(),
        new LoggingTransparencyLog(new InMemoryNetworkTransparencyLog(), factory),
        _ => new GitFetchResult(4096));

    private sealed record Entry(LogLevel Level, string Category, string Message);

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Sink(_entries, categoryName);

        public void Dispose() { }

        private sealed class Sink : ILogger
        {
            private readonly ConcurrentQueue<Entry> _entries;
            private readonly string _category;

            public Sink(ConcurrentQueue<Entry> entries, string category)
            {
                _entries = entries;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _entries.Enqueue(new Entry(logLevel, _category, formatter(state, exception)));
        }
    }
}
