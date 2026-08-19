using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Mainguard.Git.Models;

namespace Mainguard.Git.Audit;

/// <summary>
/// The P2-15 tamper-evident audit log: canonical-JSON hash chain over an append-only SQLite table
/// (AES-GCM payloads at rest) plus a payload-free append-only file mirror.
///
/// <para><b>What is hashed:</b> not the caller payload alone but the canonical ENVELOPE
/// <c>{identity, payload, seq, timestamp, type}</c> — the TI-P2-15 tamper sweep requires a flipped
/// timestamp (or type, or seq) column to fail verification, which only holds if those fields are
/// under the hash. <see cref="VerifyAll"/> additionally cross-checks the plaintext query columns
/// against the decrypted envelope so a column-only edit is caught too.</para>
///
/// <para><b>Crash ordering:</b> the SQLite transaction commits first (DB is truth), the mirror is
/// appended second; <see cref="AuditFileMirror.Recover"/> repairs a torn/missing mirror tail at
/// open. A CONTENT disagreement in the intact mirror prefix is never repaired — it is evidence,
/// surfaced as a verification failure at that seq (auto-"repairing" it would let an attacker who
/// rewrote only the DB have the witness quietly rewritten to match).</para>
///
/// <para><b>Append throws on store failure</b> — deliberately. RT-D3 callers that must never block
/// on audit availability (the kill switch) already catch and arm their gap-marker path; swallowing
/// here would turn every audit outage into silent record loss instead.</para>
/// </summary>
public sealed class ChainedAuditLog : IChainedAuditLog
{
    /// <summary>The tombstone that replaces <c>PayloadJson</c> on a redacted record's surface.</summary>
    public const string RedactedTombstone = "{\"redacted\":true}";

    /// <summary>Event type appended by <see cref="Redact"/> (and retention, which rides it).</summary>
    public const string RedactionEventType = "redaction";

    private readonly Func<AppDbContext> _dbFactory;
    private readonly AuditCrypto _crypto;
    private readonly AuditFileMirror _mirror;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private long? _mirrorConflictSeq;

    /// <summary>Test-only fault seam (TI-P2-15 item 3): runs between the DB commit and the mirror
    /// append, where a crash leaves the two media maximally divergent.</summary>
    internal Action? FaultBetweenDbAndMirror { get; set; }

    public ChainedAuditLog(
        Func<AppDbContext> dbFactory,
        AuditCrypto crypto,
        AuditFileMirror mirror,
        Func<DateTimeOffset>? clock = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        using var db = _dbFactory();
        var rows = db.AuditRecords.OrderBy(r => r.Seq)
            .Select(r => new AuditFileMirror.MirrorRecord(r.Seq, r.TimestampText, r.Type, r.PrevHash, r.Hash))
            .ToList();

        var recovery = _mirror.Recover(rows);
        _mirrorConflictSeq = recovery.ConflictSeq;
    }

    // ---- the narrow IAuditLog seam (28 call sites, AuditProbe) ----

    /// <inheritdoc />
    public void Append(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var identity = auditEvent.Fields.TryGetValue("os_identity", out var id) ? id : "daemon";
        Append(auditEvent.Type, auditEvent.Fields, identity);
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEvent> Read()
    {
        var events = new List<AuditEvent>();
        foreach (var record in Read(1, int.MaxValue))
        {
            if (record.PayloadJson == RedactedTombstone)
            {
                events.Add(new AuditEvent(record.Type, new Dictionary<string, string> { ["redacted"] = "true" }));
                continue;
            }

            var fields = new Dictionary<string, string>();
            using var doc = JsonDocument.Parse(record.PayloadJson);
            if (doc.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in payload.EnumerateObject())
                {
                    fields[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()!
                        : property.Value.GetRawText();
                }
            }

            events.Add(new AuditEvent(record.Type, fields));
        }

        return events;
    }

    // ---- the P2-15 contract surface ----

    /// <inheritdoc />
    public long Append(string type, object payload, string osIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(osIdentity);

        lock (_gate)
        {
            return AppendLocked(type, payload, osIdentity);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditRecord> Read(long fromSeq, int take)
    {
        lock (_gate)
        {
            using var db = _dbFactory();
            return db.AuditRecords
                .Where(r => r.Seq >= fromSeq)
                .OrderBy(r => r.Seq)
                .Take(take)
                .AsEnumerable()
                .Select(Materialize)
                .ToList();
        }
    }

    /// <inheritdoc />
    public (long Seq, string Hash)? Head()
    {
        lock (_gate)
        {
            using var db = _dbFactory();
            var head = db.AuditRecords.OrderByDescending(r => r.Seq).FirstOrDefault();
            return head is null ? null : (head.Seq, head.Hash);
        }
    }

    /// <inheritdoc />
    public (bool Valid, long? FirstBadSeq) VerifyAll()
    {
        lock (_gate)
        {
            using var db = _dbFactory();
            var rows = db.AuditRecords.OrderBy(r => r.Seq).ToList();

            long? firstBad = null;
            void Flag(long seq)
            {
                if (firstBad is null || seq < firstBad)
                {
                    firstBad = seq;
                }
            }

            var prevHash = HashChain.GenesisHash;
            var expectedSeq = 1L;
            var redactedRows = new List<AuditRecordRow>();
            var redactionVouchers = new List<(long OriginalSeq, string OriginalHash)>();

            foreach (var row in rows)
            {
                if (row.Seq != expectedSeq || row.PrevHash != prevHash)
                {
                    Flag(row.Seq);
                    // Chain linkage is broken here; keep walking from the row's own claim so later
                    // independent tampering still surfaces the EARLIEST bad seq, not a cascade.
                    prevHash = row.Hash;
                    expectedSeq = row.Seq + 1;
                    continue;
                }

                if (row.Redacted)
                {
                    if (row.PayloadCiphertext is not null)
                    {
                        Flag(row.Seq); // a "redacted" row still carrying payload is a rewrite attempt
                    }
                    else
                    {
                        redactedRows.Add(row);
                    }
                }
                else if (!TryDecryptEnvelope(row, out var envelope)
                    || HashChain.ComputeHash(row.PrevHash, envelope) != row.Hash
                    || !ColumnsMatchEnvelope(row, envelope, out var voucher))
                {
                    Flag(row.Seq);
                }
                else if (voucher is not null)
                {
                    redactionVouchers.Add(voucher.Value);
                }

                prevHash = row.Hash;
                expectedSeq = row.Seq + 1;
            }

            // A redacted row's hash cannot be recomputed (payload destroyed); it must be vouched
            // for by a chained redaction event that recorded the original hash before tombstoning.
            foreach (var row in redactedRows)
            {
                if (!redactionVouchers.Any(v => v.OriginalSeq == row.Seq && v.OriginalHash == row.Hash))
                {
                    Flag(row.Seq);
                }
            }

            // The mirror witness: a content disagreement (at open, or now) fails at that seq.
            if (_mirrorConflictSeq is long openConflict)
            {
                Flag(openConflict);
            }

            // Seq-keyed, not positional: concurrent in-proc hosts (the test tier) interleave mirror
            // appends out of order, and a crash-recovery backfill can duplicate one — an IDENTICAL
            // duplicate is benign, a DIFFERING one is tamper.
            var mirrorBySeq = new Dictionary<long, AuditFileMirror.MirrorRecord>();
            long mirrorHead = 0;
            foreach (var m in _mirror.ReadAll())
            {
                if (mirrorBySeq.TryGetValue(m.Seq, out var existing))
                {
                    if (existing != m)
                    {
                        Flag(m.Seq);
                    }
                }
                else
                {
                    mirrorBySeq[m.Seq] = m;
                    mirrorHead = Math.Max(mirrorHead, m.Seq);
                }
            }

            var rowSeqs = new HashSet<long>();
            foreach (var r in rows)
            {
                rowSeqs.Add(r.Seq);
                if (!mirrorBySeq.TryGetValue(r.Seq, out var m))
                {
                    // Missing at the TAIL is a not-yet-flushed append (crash lag / in-flight writer)
                    // — recovery backfills it at next open. A HOLE below the mirror head is not.
                    if (r.Seq < mirrorHead)
                    {
                        Flag(r.Seq);
                    }

                    continue;
                }

                if (m.TimestampText != r.TimestampText || m.Type != r.Type
                    || m.PrevHash != r.PrevHash || m.Hash != r.Hash)
                {
                    Flag(r.Seq);
                }
            }

            foreach (var seq in mirrorBySeq.Keys)
            {
                if (!rowSeqs.Contains(seq))
                {
                    Flag(seq); // mirror claims a record the DB does not have
                }
            }

            return (firstBad is null, firstBad);
        }
    }

    /// <inheritdoc />
    public long Redact(long seq, string reason, string osIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentNullException.ThrowIfNull(osIdentity);

        lock (_gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var db = _dbFactory();
                var row = db.AuditRecords.SingleOrDefault(r => r.Seq == seq)
                    ?? throw new ArgumentException($"No audit record with seq {seq}.", nameof(seq));
                if (row.Redacted)
                {
                    throw new InvalidOperationException($"Audit record {seq} is already redacted.");
                }

                if (row.Type == RedactionEventType)
                {
                    // A redaction event is the only thing vouching for its target's hash — redacting
                    // it would orphan that record and fail the whole chain. Its payload carries no
                    // prompt content, so retention exempts it too.
                    throw new InvalidOperationException("Redaction events cannot themselves be redacted.");
                }

                // The redaction event is appended FIRST, then the original is tombstoned — one SQLite
                // transaction, so a crash leaves either both or neither (the append-only trigger
                // allows exactly this tombstone transition and nothing else).
                AuditFileMirror.MirrorRecord mirrorRecord;
                long redactionSeq;
                try
                {
                    using var transaction = db.Database.BeginTransaction();
                    redactionSeq = AppendRowWithin(db, RedactionEventType, new Dictionary<string, object>
                    {
                        ["original_hash"] = row.Hash,
                        ["original_seq"] = row.Seq,
                        ["reason"] = reason,
                    }, osIdentity, out mirrorRecord);

                    row.PayloadCiphertext = null;
                    row.KeyId = null;
                    row.Redacted = true;
                    db.SaveChanges();
                    transaction.Commit();
                }
                catch (Exception ex) when (IsHeadRace(ex) && attempt < AppendRetries)
                {
                    continue;
                }

                FaultBetweenDbAndMirror?.Invoke();
                _mirror.Append(mirrorRecord);
                return redactionSeq;
            }
        }
    }

    /// <summary>
    /// P2-15 retention (default 90 d): every record older than <paramref name="retention"/> is
    /// REDACTED — payload tombstoned via the chained-event path, row count unchanged, chain intact.
    /// Never deletion. Redaction events are exempt (payload is only seq/hash/reason, and they vouch
    /// for earlier tombstones). Returns the number of records redacted.
    /// </summary>
    public int ApplyRetention(TimeSpan retention)
    {
        var cutoff = _clock() - retention;
        List<long> expired;
        lock (_gate)
        {
            using var db = _dbFactory();
            expired = db.AuditRecords
                .Where(r => !r.Redacted && r.Type != RedactionEventType)
                .AsEnumerable()
                .Where(r => DateTimeOffset.Parse(r.TimestampText, CultureInfo.InvariantCulture) < cutoff)
                .Select(r => r.Seq)
                .ToList();
        }

        foreach (var seq in expired)
        {
            Redact(seq, "retention-expiry", "daemon");
        }

        return expired.Count;
    }

    // ---- internals ----

    /// <summary>
    /// How often an append retries after losing a head race. The daemon proper is this store's only
    /// writer, but the in-proc test tier runs several daemon hosts over ONE run-scoped DB in
    /// parallel — the head is therefore re-read inside every attempt (never cached across appends)
    /// and a primary-key collision means another writer chained a record first: re-read, re-hash,
    /// re-insert.
    /// </summary>
    private const int AppendRetries = 8;

    private long AppendLocked(string type, object payload, string osIdentity)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var db = _dbFactory();
            AuditFileMirror.MirrorRecord mirrorRecord;
            long seq;
            try
            {
                seq = AppendRowWithin(db, type, payload, osIdentity, out mirrorRecord);
                db.SaveChanges();
            }
            catch (Exception ex) when (IsHeadRace(ex) && attempt < AppendRetries)
            {
                continue;
            }

            FaultBetweenDbAndMirror?.Invoke();
            _mirror.Append(mirrorRecord);
            return seq;
        }
    }

    /// <summary>Stages one chained row on <paramref name="db"/>, reading the CURRENT head from that
    /// context (caller saves/commits). The mirror record is returned to append AFTER commit.</summary>
    private long AppendRowWithin(
        AppDbContext db, string type, object payload, string osIdentity,
        out AuditFileMirror.MirrorRecord mirrorRecord)
    {
        var head = db.AuditRecords.OrderByDescending(r => r.Seq).FirstOrDefault();
        var headSeq = head?.Seq ?? 0;
        var headHash = head?.Hash ?? HashChain.GenesisHash;

        var seq = headSeq + 1;
        var timestamp = _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var envelope = CanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["identity"] = osIdentity,
            ["payload"] = payload,
            ["seq"] = seq,
            ["timestamp"] = timestamp,
            ["type"] = type,
        });
        var hash = HashChain.ComputeHash(headHash, envelope);

        db.AuditRecords.Add(new AuditRecordRow
        {
            Seq = seq,
            TimestampText = timestamp,
            Type = type,
            PayloadCiphertext = _crypto.Encrypt(envelope),
            KeyId = AuditCrypto.CurrentKeyId,
            PrevHash = headHash,
            Hash = hash,
            Redacted = false,
        });

        mirrorRecord = new AuditFileMirror.MirrorRecord(seq, timestamp, type, headHash, hash);
        return seq;
    }

    /// <summary>A lost append race: another writer took our seq (PK constraint) or holds the write
    /// lock (SQLITE_BUSY/LOCKED). Anything else — including the append-only trigger's ABORT — is a
    /// real failure and propagates.</summary>
    private static bool IsHeadRace(Exception ex)
    {
        var sqlite = ex as Microsoft.Data.Sqlite.SqliteException
            ?? ex.InnerException as Microsoft.Data.Sqlite.SqliteException;
        if (sqlite is null)
        {
            return false;
        }

        // 19 = SQLITE_CONSTRAINT — but the trigger's RAISE(ABORT) surfaces as a constraint error
        // too, so only a PRIMARY KEY violation counts as a race.
        return sqlite.SqliteErrorCode is 5 or 6
            || (sqlite.SqliteErrorCode == 19 && sqlite.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
    }

    private AuditRecord Materialize(AuditRecordRow row)
    {
        var payloadJson = row.Redacted || row.PayloadCiphertext is null
            ? RedactedTombstone
            : _crypto.Decrypt(row.PayloadCiphertext);
        return new AuditRecord(
            row.Seq,
            DateTimeOffset.Parse(row.TimestampText, CultureInfo.InvariantCulture),
            row.Type,
            payloadJson,
            row.PrevHash,
            row.Hash);
    }

    private bool TryDecryptEnvelope(AuditRecordRow row, out string envelope)
    {
        envelope = string.Empty;
        if (row.PayloadCiphertext is null)
        {
            return false;
        }

        try
        {
            envelope = _crypto.Decrypt(row.PayloadCiphertext);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Cross-checks the plaintext query columns against the hashed envelope, and extracts
    /// the (originalSeq, originalHash) voucher when the envelope is a redaction event's.</summary>
    private static bool ColumnsMatchEnvelope(
        AuditRecordRow row, string envelope, out (long OriginalSeq, string OriginalHash)? voucher)
    {
        voucher = null;
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            var root = doc.RootElement;
            if (root.GetProperty("seq").GetInt64() != row.Seq
                || root.GetProperty("timestamp").GetString() != row.TimestampText
                || root.GetProperty("type").GetString() != row.Type)
            {
                return false;
            }

            if (row.Type == RedactionEventType
                && root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("original_seq", out var originalSeq)
                && payload.TryGetProperty("original_hash", out var originalHash))
            {
                voucher = (originalSeq.GetInt64(), originalHash.GetString()!);
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
