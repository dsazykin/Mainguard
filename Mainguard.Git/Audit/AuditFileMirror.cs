using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Mainguard.Git.Audit;

/// <summary>
/// The P2-15 append-only FILE mirror: a second medium carrying the chain columns of every record
/// (seq, timestamp, type, prevHash, hash) as length-prefixed, fsync'd JSON — deliberately WITHOUT
/// payloads, so redaction never has a second copy to chase and prompt content exists on disk in
/// exactly one (encrypted, tombstonable) place. Its job is integrity witness + crash forensics:
/// tampering with the SQLite file alone now has to also rewrite this file coherently, and a torn
/// tail here is the fingerprint of a crash between DB commit and mirror append.
/// </summary>
public sealed class AuditFileMirror
{
    /// <summary>One mirrored record — the chain columns only, never a payload.</summary>
    public sealed record MirrorRecord(long Seq, string TimestampText, string Type, string PrevHash, string Hash);

    private readonly string _path;

    public AuditFileMirror(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>The mirror file's absolute path (diagnostics + the CLI verifier).</summary>
    public string FilePath => _path;

    /// <summary>Appends one record and fsyncs — called AFTER the DB transaction commits (DB is truth).</summary>
    public void Append(MirrorRecord record)
    {
        var payload = Encode(record);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>All intact records, oldest first. A torn tail is ignored (see <see cref="Recover"/>).</summary>
    public IReadOnlyList<MirrorRecord> ReadAll()
    {
        var records = new List<MirrorRecord>();
        if (!File.Exists(_path))
        {
            return records;
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var prefix = new byte[4];
        while (true)
        {
            if (!TryReadExactly(stream, prefix))
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length <= 0 || length > 1 << 20)
            {
                break; // corrupt length — treat the rest as torn
            }

            var payload = new byte[length];
            if (!TryReadExactly(stream, payload))
            {
                break; // torn tail
            }

            var record = TryDecode(payload);
            if (record is null)
            {
                break;
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>The outcome of <see cref="Recover"/>: how many tail records were repaired from DB
    /// truth, and — the load-bearing half — the seq of the first CONTENT disagreement, if any.</summary>
    public sealed record RecoveryResult(int Backfilled, long? ConflictSeq);

    /// <summary>
    /// Reconciles the mirror against the DB truth on open. Two very different cases, deliberately
    /// treated differently:
    ///
    /// <para><b>Torn or missing tail</b> — a crash between DB commit and mirror append can only
    /// cost the mirror its fsync'd END: repaired silently (truncate torn bytes, backfill from DB).</para>
    ///
    /// <para><b>Content disagreement in the intact prefix, or a mirror AHEAD of the DB</b> — no
    /// crash produces either (the DB commits first), so this is evidence of one medium being
    /// rewritten. It is NEVER repaired: it comes back as <see cref="RecoveryResult.ConflictSeq"/>
    /// and fails verification at that seq. Auto-repairing would let whoever rewrote the SQLite
    /// file have this witness quietly rewritten to match.</para>
    /// </summary>
    public RecoveryResult Recover(IReadOnlyList<MirrorRecord> dbRecords)
    {
        ArgumentNullException.ThrowIfNull(dbRecords);

        var mirrored = ReadAll();
        var overlap = Math.Min(mirrored.Count, dbRecords.Count);
        for (var i = 0; i < overlap; i++)
        {
            if (mirrored[i] != dbRecords[i])
            {
                return new RecoveryResult(0, mirrored[i].Seq);
            }
        }

        if (mirrored.Count > dbRecords.Count)
        {
            return new RecoveryResult(0, mirrored[dbRecords.Count].Seq);
        }

        if (FileHasTornTail(mirrored))
        {
            RewriteAll(dbRecords);
            return new RecoveryResult(dbRecords.Count - mirrored.Count, null);
        }

        var appended = 0;
        for (var i = mirrored.Count; i < dbRecords.Count; i++)
        {
            Append(dbRecords[i]);
            appended++;
        }

        return new RecoveryResult(appended, null);
    }

    private bool FileHasTornTail(IReadOnlyList<MirrorRecord> intact)
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        long intactBytes = 0;
        foreach (var record in intact)
        {
            intactBytes += 4 + Encode(record).Length;
        }

        return new FileInfo(_path).Length > intactBytes;
    }

    private void RewriteAll(IReadOnlyList<MirrorRecord> records)
    {
        var temp = _path + ".rebuild";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> prefix = stackalloc byte[4];
            foreach (var record in records)
            {
                var payload = Encode(record);
                BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
                stream.Write(prefix);
                stream.Write(payload);
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static byte[] Encode(MirrorRecord record)
        => Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new Dictionary<string, object>
        {
            ["hash"] = record.Hash,
            ["prev"] = record.PrevHash,
            ["seq"] = record.Seq,
            ["ts"] = record.TimestampText,
            ["type"] = record.Type,
        }));

    private static MirrorRecord? TryDecode(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return new MirrorRecord(
                root.GetProperty("seq").GetInt64(),
                root.GetProperty("ts").GetString()!,
                root.GetProperty("type").GetString()!,
                root.GetProperty("prev").GetString()!,
                root.GetProperty("hash").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
