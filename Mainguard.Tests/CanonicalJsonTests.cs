using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-15 item 2 (canonicalization half): semantically equal payloads must serialize to
/// byte-identical canonical JSON regardless of key order, ambient culture, or numeric spelling —
/// the hash chain is only as stable as these bytes.
/// </summary>
public class CanonicalJsonTests
{
    [Fact]
    public void Serialize_ShouldSortKeysOrdinally()
    {
        var shuffled = new Dictionary<string, object> { ["zeta"] = 1, ["Alpha"] = 2, ["alpha"] = 3, ["beta"] = 4 };
        // Ordinal order: uppercase before lowercase ("Alpha" < "alpha" < "beta" < "zeta").
        Assert.Equal("{\"Alpha\":2,\"alpha\":3,\"beta\":4,\"zeta\":1}", CanonicalJson.Serialize(shuffled));
    }

    [Fact]
    public void Serialize_ShouldSortNestedObjectKeys()
    {
        var payload = new Dictionary<string, object>
        {
            ["outer_b"] = new Dictionary<string, object> { ["y"] = 1, ["x"] = 2 },
            ["outer_a"] = new[] { new Dictionary<string, object> { ["k2"] = "v", ["k1"] = "u" } },
        };
        Assert.Equal(
            "{\"outer_a\":[{\"k1\":\"u\",\"k2\":\"v\"}],\"outer_b\":{\"x\":2,\"y\":1}}",
            CanonicalJson.Serialize(payload));
    }

    [Fact]
    public void Serialize_ShouldBeCultureInvariant_UnderTurkishCulture()
    {
        // The tr-TR test the plan names: dotted/dotless-i casing and comma decimal separators are
        // the classic ways ambient culture corrupts "canonical" output.
        var payload = new Dictionary<string, object> { ["Index"] = 1, ["ratio"] = 1.5, ["II"] = "İstanbul" };
        var invariant = CanonicalJson.Serialize(payload);

        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(invariant, CanonicalJson.Serialize(payload));
            Assert.Contains("1.5", CanonicalJson.Serialize(payload)); // never "1,5"
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Serialize_ShouldCollapseEquivalentNumberSpellings()
    {
        Assert.Equal(CanonicalJson.Serialize(new { n = 1.0 }), CanonicalJson.Serialize(new { n = 1 }));
        Assert.Equal("{\"n\":1}", CanonicalJson.Serialize(new { n = 1.0 }));
        Assert.Equal("{\"n\":0.5}", CanonicalJson.Serialize(new { n = 0.5 }));
        Assert.Equal("{\"n\":-3}", CanonicalJson.Serialize(new { n = -3L }));
    }

    [Fact]
    public void Serialize_ShouldBeStable_AcrossInputShapes()
    {
        // The same semantic payload built three different ways → identical bytes.
        var fromAnonymous = CanonicalJson.Serialize(new { type = "spawn", agent = "a-1" });
        var fromDictionary = CanonicalJson.Serialize(new Dictionary<string, string> { ["agent"] = "a-1", ["type"] = "spawn" });
        var fromSorted = CanonicalJson.Serialize(new SortedDictionary<string, string> { ["type"] = "spawn", ["agent"] = "a-1" });
        Assert.Equal(fromAnonymous, fromDictionary);
        Assert.Equal(fromDictionary, fromSorted);
    }

    [Fact]
    public void Serialize_ShouldHandleNullsAndBooleans()
    {
        Assert.Equal("{\"a\":null,\"b\":true,\"c\":false}", CanonicalJson.Serialize(
            new Dictionary<string, object?> { ["c"] = false, ["a"] = null, ["b"] = true }));
        Assert.Equal("null", CanonicalJson.Serialize(null));
    }

    [Fact]
    public void Serialize_ShouldEscapeStringsDeterministically()
    {
        var a = CanonicalJson.Serialize(new { s = "line\n\"quote\" \\ ünïcode" });
        var b = CanonicalJson.Serialize(new { s = "line\n\"quote\" \\ ünïcode" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void SerializeToUtf8Bytes_ShouldEmitNoBom()
    {
        var bytes = CanonicalJson.SerializeToUtf8Bytes(new { a = 1 });
        Assert.Equal((byte)'{', bytes[0]);
    }
}
