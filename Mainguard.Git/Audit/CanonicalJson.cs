using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Mainguard.Git.Audit;

/// <summary>
/// P2-15 canonical JSON: the one serialization the hash chain is computed over. Two payloads that
/// are semantically equal must produce byte-identical output regardless of property order, ambient
/// culture, or how the caller built the object — hashing non-canonical JSON is a spec-level
/// rejection trigger (master doc §P2-15), because a hash over unstable bytes yields false tamper
/// alarms that train operators to ignore the one alarm that matters.
///
/// <para>Rules: object keys sorted ordinally (byte order, not culture order); invariant culture for
/// every number; no insignificant whitespace; UTF-8 without BOM; <see cref="DateTimeOffset"/> as
/// round-trip ("O") strings. Numbers are re-emitted through <c>decimal</c>/<c>double</c> invariant
/// formatting so <c>1.0</c> and <c>1.00</c> collapse to one representation.</para>
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        // The payload is serialized as the caller shaped it — canonicalization happens on the
        // re-emit pass below, so these options only need to produce *valid* JSON to re-walk.
        WriteIndented = false,
    };

    /// <summary>Serializes <paramref name="payload"/> to canonical JSON text (see class remarks).</summary>
    public static string Serialize(object? payload)
    {
        // Round-trip through JsonElement so any input shape (anonymous types, dictionaries,
        // records, JsonElement itself) walks one canonical emitter.
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload, SerializeOptions));
        var sb = new StringBuilder();
        WriteCanonical(doc.RootElement, sb);
        return sb.ToString();
    }

    /// <summary>Canonical UTF-8 bytes (no BOM) — the exact input to <see cref="HashChain.ComputeHash"/>.</summary>
    public static byte[] SerializeToUtf8Bytes(object? payload) => Encoding.UTF8.GetBytes(Serialize(payload));

    private static void WriteCanonical(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                // Ordinal sort: culture-independent byte order. String.CompareOrdinal, never the
                // current culture — the tr-TR 'i' family is the classic corruption here.
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(property.Name, sb);
                    sb.Append(':');
                    WriteCanonical(property.Value, sb);
                }

                sb.Append('}');
                break;

            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    WriteCanonical(item, sb);
                }

                sb.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(element.GetString()!, sb);
                break;

            case JsonValueKind.Number:
                WriteNumber(element, sb);
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteNumber(JsonElement element, StringBuilder sb)
    {
        // Collapse equal numeric values to one representation: integers without a fraction emit as
        // integers ("1.0" → "1"); everything else goes through invariant round-trip formatting.
        if (element.TryGetInt64(out var l))
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (element.TryGetDecimal(out var m))
        {
            if (decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue)
            {
                sb.Append(((long)m).ToString(CultureInfo.InvariantCulture));
                return;
            }

            sb.Append(m.ToString(CultureInfo.InvariantCulture));
            return;
        }

        sb.Append(element.GetDouble().ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(string value, StringBuilder sb)
    {
        // System.Text.Json's strict escaper is deterministic for a given input; route through it so
        // escaping never diverges from what a standards-compliant reader expects.
        sb.Append(JsonSerializer.Serialize(value, SerializeOptions));
    }
}
