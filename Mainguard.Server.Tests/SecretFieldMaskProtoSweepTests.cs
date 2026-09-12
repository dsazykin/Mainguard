using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mainguard.Server.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F56 — the sweep the per-message tests cannot be: EVERY message in the gRPC contract, every string
/// and bytes field in it filled with a sentinel, run through <see cref="SecretFieldMask.Summarize"/>,
/// and the sentinel asserted absent from the result.
///
/// <para>This is the one test that survives the contract changing. The hand-written cases pin the
/// messages the audit named; this one fails the day someone adds a field that carries content, adds
/// a message type nobody thought about, or widens <c>LoggableNames</c> — which is the failure mode a
/// denylist has and an allowlist is supposed to remove. It is a test ABOUT the allowlist rule, so it
/// deliberately does not enumerate the messages it covers: it asks the descriptor pool.</para>
///
/// <para>The sentinel is longer than the 64-character ceiling on a renderable identifier, so even a
/// field whose NAME is allowlisted must render it as a length. Nothing in the contract legitimately
/// carries a 96-character handle.</para>
/// </summary>
public sealed class SecretFieldMaskProtoSweepTests
{
    /// <summary>Identifier characters only — so nothing but the LENGTH rule can be what rejects it.
    /// A sentinel full of punctuation would pass for the wrong reason.</summary>
    private const string Sentinel =
        "SENTINELdeadbeefSENTINELdeadbeefSENTINELdeadbeefSENTINELdeadbeefSENTINELdeadbeefSENTINELdeadbeef";

    public static TheoryData<string> AllMessages()
    {
        var data = new TheoryData<string>();
        foreach (var descriptor in EveryMessage())
        {
            data.Add(descriptor.FullName);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void EveryStringAndBytesField_InEveryMessage_IsWithheldFromTheSummary(string messageFullName)
    {
        var descriptor = EveryMessage().Single(d => d.FullName == messageFullName);
        var message = descriptor.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
        var filled = Fill(message, depth: 0);

        var summary = SecretFieldMask.Summarize(message);

        Assert.DoesNotContain(Sentinel, summary, StringComparison.Ordinal);
        // A prefix would leak just as surely as the whole value; assert on a short head of it too.
        Assert.DoesNotContain(Sentinel[..16], summary, StringComparison.Ordinal);
        Assert.StartsWith(descriptor.Name + "{", summary, StringComparison.Ordinal);
        // Sanity: the sweep is only meaningful if it actually put the sentinel somewhere.
        if (descriptor.Fields.InDeclarationOrder().Any(IsTextual))
        {
            Assert.True(filled, $"{messageFullName} has textual fields but none were filled");
        }
    }

    /// <summary>
    /// The other half of the guarantee. The long sentinel above proves the LENGTH rule; this proves
    /// the NAME rule, which is the one that moves: a value short enough and tame enough to be
    /// rendered is rendered only if its field name is on the allowlist, and this pins that set
    /// against the whole contract.
    ///
    /// <para>If someone adds <c>"reason"</c> or <c>"title"</c> to <c>LoggableNames</c> — the natural
    /// thing to do while debugging — this test names the field they let through. That is the failure
    /// no per-message test can be relied on to produce, because the leak arrives in a message whose
    /// test nobody thought to write.</para>
    /// </summary>
    [Fact]
    public void OnlyTheAllowlistedFieldNames_EverRenderTheirValue()
    {
        // Short, and identifier-only: nothing but the NAME rule can reject it.
        const string handle = "sentinel-0123";

        var rendered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in EveryMessage())
        {
            var message = descriptor.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
            FillStrings(message, handle, depth: 0);
            var summary = SecretFieldMask.Summarize(message);
            foreach (var field in descriptor.Fields.InDeclarationOrder())
            {
                if (field is { FieldType: FieldType.String, IsRepeated: false, IsMap: false }
                    && summary.Contains($"{field.Name}={handle}", StringComparison.Ordinal))
                {
                    rendered.Add(field.Name);
                }
            }
        }

        // The allowlist, restated here on purpose: opaque correlation handles and closed-set
        // discriminators, nothing a human typed. A name arriving in this set is a decision, and the
        // diff is where it should be argued.
        var allowed = new SortedSet<string>(StringComparer.Ordinal)
        {
            "agent_id", "agent_kind", "repo_handle", "repo_hash", "lease_id", "session_id",
            "request_id", "correlation_id", "task_id", "worker_id", "role", "kind", "state",
            "phase", "engine", "key_id",
        };

        Assert.Empty(rendered.Except(allowed, StringComparer.Ordinal));
        // ...and the sweep really did exercise the rule rather than rendering nothing at all.
        Assert.NotEmpty(rendered);
    }

    /// <summary>The contract is not empty and the sweep is not vacuous — a reflection walk that
    /// silently found nothing would pass every assertion above.</summary>
    [Fact]
    public void TheSweep_CoversTheWholeContract()
    {
        var messages = EveryMessage().ToList();

        Assert.True(messages.Count > 50, $"only {messages.Count} messages discovered — the walk is not finding the contract");
        Assert.Contains(messages, m => m.FullName == "mainguard.v1.SpawnAgentRequest");
        Assert.Contains(messages, m => m.FullName == "mainguard.v1.ReadAuditResponse");
    }

    // ---- the reflection walk ----

    private static bool IsTextual(FieldDescriptor field)
        => field.FieldType is FieldType.String or FieldType.Bytes;

    /// <summary>Every message type in every proto file of the contract assembly, nested types included.</summary>
    private static IEnumerable<MessageDescriptor> EveryMessage()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EveryFile())
        {
            foreach (var message in Flatten(file.MessageTypes))
            {
                if (seen.Add(message.FullName))
                {
                    yield return message;
                }
            }
        }
    }

    private static IEnumerable<FileDescriptor> EveryFile()
    {
        // The generated `*Reflection` classes each expose their FileDescriptor as a static property.
        var assembly = typeof(Mainguard.Protos.V1.SpawnAgentRequest).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.Name.EndsWith("Reflection", StringComparison.Ordinal)))
        {
            var property = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
            if (property?.GetValue(null) is FileDescriptor file)
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<MessageDescriptor> Flatten(IEnumerable<MessageDescriptor> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            foreach (var nested in Flatten(message.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Puts the sentinel in every string/bytes field the message has — singular, repeated, map values
    /// and (shallowly) nested messages. Returns whether anything was actually filled.
    /// </summary>
    private static bool Filled(bool a, bool b) => a || b;

    /// <summary>Sets every singular string field to <paramref name="value"/>, recursing into nested
    /// messages. Used by the name-rule sweep, where only singular strings can render at all.</summary>
    private static void FillStrings(IMessage message, string value, int depth)
    {
        if (depth > 3)
        {
            return;
        }

        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            try
            {
                if (field.IsRepeated || field.IsMap)
                {
                    continue;
                }

                if (field.FieldType == FieldType.String)
                {
                    field.Accessor.SetValue(message, value);
                }
                else if (field.FieldType == FieldType.Message)
                {
                    var nested = field.MessageType.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
                    FillStrings(nested, value, depth + 1);
                    field.Accessor.SetValue(message, nested);
                }
            }
            catch (Exception)
            {
                // Same reasoning as Fill: a field reflection cannot set is one the summary will not
                // render either.
            }
        }
    }

    private static bool Fill(IMessage message, int depth)
    {
        // Deeper than Summarize descends: a value the summary would never reach cannot leak, but
        // filling one costs nothing and guards the depth cap itself.
        if (depth > 3)
        {
            return false;
        }

        var filled = false;
        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            // A oneof member can only be set one at a time; setting each in turn is fine (the last
            // wins), and every member is still covered by its own message's sweep.
            try
            {
                filled = Filled(filled, FillField(message, field, depth));
            }
            catch (Exception)
            {
                // A field reflection cannot set is a field the summary will not render either.
            }
        }

        return filled;
    }

    private static bool FillField(IMessage message, FieldDescriptor field, int depth)
    {
        if (field.IsMap)
        {
            var map = (IDictionary)field.Accessor.GetValue(message);
            var entry = field.MessageType;
            var keyField = entry.FindFieldByNumber(1);
            var valueField = entry.FindFieldByNumber(2);
            if (keyField.FieldType != FieldType.String)
            {
                return false;
            }

            switch (valueField.FieldType)
            {
                case FieldType.String:
                    map["k"] = Sentinel;
                    return true;
                case FieldType.Bytes:
                    map["k"] = ByteString.CopyFromUtf8(Sentinel);
                    return true;
                default:
                    return false;
            }
        }

        if (field.IsRepeated)
        {
            var list = (IList)field.Accessor.GetValue(message);
            switch (field.FieldType)
            {
                case FieldType.String:
                    list.Add(Sentinel);
                    return true;
                case FieldType.Bytes:
                    list.Add(ByteString.CopyFromUtf8(Sentinel));
                    return true;
                case FieldType.Message:
                    var element = field.MessageType.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
                    var elementFilled = Fill(element, depth + 1);
                    list.Add(element);
                    return elementFilled;
                default:
                    return false;
            }
        }

        switch (field.FieldType)
        {
            case FieldType.String:
                field.Accessor.SetValue(message, Sentinel);
                return true;
            case FieldType.Bytes:
                field.Accessor.SetValue(message, ByteString.CopyFromUtf8(Sentinel));
                return true;
            case FieldType.Message:
                var nested = field.MessageType.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
                var nestedFilled = Fill(nested, depth + 1);
                field.Accessor.SetValue(message, nested);
                return nestedFilled;
            default:
                return false;
        }
    }
}
