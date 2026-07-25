using System;
using System.IO;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-40: the pin-override file is validated on READ, not only on write. <c>pin-overrides.json</c> is a
/// plain user-writable JSON file, so "we validated it when we wrote it" says nothing about the bytes that
/// come back — a hand-edited (or merge-mangled, or attacker-planted) entry could re-introduce exactly what
/// the pin discipline exists to forbid: a floating version, a plaintext payload URL, a truncated hash. An
/// invalid entry is dropped so the bundled manifest pin applies; a valid one still round-trips.
/// </summary>
public class AdapterPinOverrideStoreTests : IDisposable
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mainguard-pin-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "pin-overrides.json");

    public AdapterPinOverrideStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* never fail a test from cleanup */ }
    }

    private void WriteFile(string json) => File.WriteAllText(Path_, json);

    [Fact]
    public void ValidOverride_RoundTrips()
    {
        var store = new FileAdapterPinOverrideStore(Path_);
        store.Set("tool", new AdapterPinOverride("2.0.0", "https://registry.npmjs.org/tool.tgz", Sha));

        var read = new FileAdapterPinOverrideStore(Path_).TryGet("tool");

        Assert.NotNull(read);
        Assert.Equal("2.0.0", read!.Version);
    }

    [Theory]
    // A floating version — the whole point of the manifest is that this cannot reach an install.
    [InlineData("latest", "https://registry.npmjs.org/tool.tgz", Sha)]
    [InlineData("1.x", "https://registry.npmjs.org/tool.tgz", Sha)]
    // A plaintext channel defeats the hash pin (the bytes can be swapped in flight).
    [InlineData("2.0.0", "http://registry.npmjs.org/tool.tgz", Sha)]
    // A hash that cannot be a sha256 means nothing is actually verified.
    [InlineData("2.0.0", "https://registry.npmjs.org/tool.tgz", "deadbeef")]
    public void HandEditedWeakOverride_IsIgnoredOnRead(string version, string payloadUrl, string sha)
    {
        WriteFile($$"""
        { "tool": { "version": "{{version}}", "payloadUrl": "{{payloadUrl}}", "sha256": "{{sha}}" } }
        """);

        Assert.Null(new FileAdapterPinOverrideStore(Path_).TryGet("tool"));
    }

    [Fact]
    public void ValidEntriesSurvive_AWeakSiblingEntry()
    {
        // One bad hand-edit must not brick every adapter's install — only its own entry is dropped.
        WriteFile($$"""
        {
          "bad":  { "version": "latest", "payloadUrl": "https://registry.npmjs.org/bad.tgz", "sha256": "{{Sha}}" },
          "good": { "version": "3.1.4",  "payloadUrl": "https://registry.npmjs.org/good.tgz", "sha256": "{{Sha}}" }
        }
        """);

        var store = new FileAdapterPinOverrideStore(Path_);

        Assert.Null(store.TryGet("bad"));
        Assert.Equal("3.1.4", store.TryGet("good")!.Version);
    }

    [Fact]
    public void Set_StillRefusesToWriteAnUnpinnedOverride()
    {
        var store = new FileAdapterPinOverrideStore(Path_);

        Assert.Throws<ArgumentException>(() =>
            store.Set("tool", new AdapterPinOverride("latest", "https://registry.npmjs.org/tool.tgz", Sha)));
    }
}
