# P2-15 — Tamper-evident audit log: implementation evidence (2026-08-19)

Point-in-time evidence snapshot for the P2-15 work shipped on `port/macos` (commits `0d772df` …
this one), written for the eventual PR to `phase2`. The binding contract is the master doc §P2-15;
its STATUS block was updated in the same change.

## What shipped

| Piece | Where | Notes |
|---|---|---|
| Pure hash chain | `Mainguard.Git/Audit/HashChain.cs` | SHA-256(prevHash ‖ payload), lowercase hex; `Verify` reports the exact first bad seq; mid-chain slices anchor on their first record, seq 1 pins to genesis |
| Canonical JSON | `Mainguard.Git/Audit/CanonicalJson.cs` | ordinal-sorted keys (recursive), invariant culture, number-spelling collapse, UTF-8 no BOM |
| Chained store | `Mainguard.Git/Audit/ChainedAuditLog.cs` | behind the **unchanged** `IAuditLog` seam (all 28 call sites + `AuditProbe` untouched); the P2-15 contract members live on `IChainedAuditLog` verbatim. Hashes the canonical envelope `{identity, payload, seq, timestamp, type}` — a flipped timestamp/type/seq **column** fails verify (TI item 2), with a column↔envelope cross-check |
| Append-only schema | migration `AddAuditChain` | SQLite triggers: `DELETE` aborts; the only legal `UPDATE` is the redaction tombstone transition. Rewriting history requires dropping a trigger first — which the chain then catches |
| Encryption at rest | `Mainguard.Git/Audit/AuditCrypto.cs` | AES-256-GCM; master key `audit-payload-key` in the OS keyring (`SecureKeyring`), generated on first use; nonce‖tag‖ciphertext blobs, key id per row |
| File mirror | `Mainguard.Git/Audit/AuditFileMirror.cs` | payload-FREE (chain columns only — redaction has no second copy to chase), length-prefixed single-write frames, fsync'd. Recovery repairs only torn/missing tails; a content disagreement is surfaced as evidence, never auto-repaired |
| Redaction | `ChainedAuditLog.Redact` | appends a chained `redaction` event carrying the original hash, then tombstones the payload in the same transaction; `VerifyAll` still passes; ciphertext + key id destroyed at the row |
| Retention | `Runtime/AuditRetentionService.cs` | 90 d default, boot + daily, as redactions — never deletion; redaction events exempt |
| Read surface | `audit.proto` + `Services/AuditGrpcService.cs` | `VerifyAudit` / `ReadAudit` — `IAuditLog.Read()`'s first production callers; coordinator-denied at the `RoleInterceptor`; `persistent=false` flags the in-memory fallback |
| CLI verb | `Cli/AuditCommands.cs` | `mainguardd audit verify [--data <db>]`: exit 0 intact / 2 tampered (first bad seq printed) / 64 usage / 1 cannot-verify; validates stored RFC 3161 tokens structurally |
| RFC 3161 anchoring | `Mainguard.Git/Audit/Rfc3161Anchor.cs` + `Runtime/AuditAnchorService.cs` | queue (1000 records / 24 h policy, idempotent per head) + real Pkcs client; **sends nothing unless `MAINGUARD_TSA_URL` is set** (no default third-party traffic); best-effort by contract — TSA down queues and retries, appending never waits |
| RT-D3 | `KillSwitch` (pre-existing hook) + `AuditTouchpointCoverageTests` | `Append` throws on store failure (deliberate); the kill is never blocked; on recovery the chained `killswitch_audit_gap` lands in the **persisted** chain and survives a reopen |
| Daemon wiring | `GatewayServiceRegistration.RegisterAuditLog` | rides the same DB-posture decision as the gateway stores; `InMemoryAuditLog` only as the loudly-logged no-DB fallback |

Deferred (per the binding scope split): `audit replay <sha>` (composes P2-38/42/43 records that do
not exist yet) and the P2-16 SIEM feed (P2-16's own task).

## Check evidence (macOS, Apple Silicon, 2026-08-19)

Reviewer-script greps — no rewrite path in production code:

```
$ grep -rn "UPDATE audit\|DELETE FROM audit\|UPDATE AuditRecords\|DELETE FROM AuditRecords" \
    Mainguard.Git/ Mainguard.Server/ Mainguard.Agents/ --include="*.cs" \
    | grep -v Migrations/ | grep -v "RAISE(ABORT"
(no output)
```

`mainguardd audit verify` on this machine's real install (pre-chain daemon DB):

```
$ dotnet Mainguard.Server/bin/Debug/net10.0/Mainguard.Server.dll audit verify
audit verify: /Users/danielsazykin/.mainguard/mainguard-daemon.db predates the audit chain (no AuditRecords table)
chain: empty · head: 0000000000000000000000000000000000000000000000000000000000000000
OK
exit=0
```

Live RFC 3161 round-trip (`RequiresNetworkFact`, run once by hand):

```
$ MAINGUARD_NETWORK_TESTS=1 dotnet test --filter "FullyQualifiedName~AnchorRoundTrip"
Passed! 1/1   # real token from freetsa.org stored and validated against the head hash
```

Test suites:

- `HashChainTests` + `CanonicalJsonTests`: 17/17 (incl. the 100-record × 3-dimension tamper sweep
  and the tr-TR culture test)
- `AuditLogTests`: 17/17 (append/reopen, schema-level append-only, exact-seq column tamper,
  encryption-at-rest sentinel, crash-mid-append via the fault seam, torn mirror tail,
  mirror-mismatch-never-auto-repaired, redaction + retention)
- `AuditTouchpointCoverageTests`: 2/2 (scripted governance session over the REAL store → exact
  ordered event sequence → reopen → chain verified; RT-D3 outage→gap→reopen)
- `Rfc3161AnchorTests`: 3/3 + 1 network-gated (run green once, see above)
- `AuditRpcTests`: 6/6 (RPC pair, coordinator denial, CLI exit contract)
- Full non-Docker `Mainguard.Server.Tests`: **527 passed** with the chained log live in every
  in-proc daemon host
- Full `Mainguard.Tests` (with libvterm required): **3342 passed, 0 failed, 19 skipped** (local,
  2026-08-19; CI on `port/macos` is the authoritative record)

## Two findings worth knowing about

1. **TI-P2-15's tamper sweep flips timestamp bytes, so the hash must cover more than the caller
   payload.** The chain therefore hashes the canonical envelope `{identity, payload, seq,
   timestamp, type}`, and `VerifyAll` cross-checks the plaintext query columns against the
   decrypted envelope — a column-only edit is caught even though the columns exist purely for
   querying.

2. **The in-proc test tier runs many daemon hosts over ONE run-scoped SQLite DB** (the
   `DaemonFixture` `Daemon:TokenPath` setting does not reach `ConfigureServices` in the
   WebApplicationFactory flow, so the DB falls back to the module-initializer
   `MAINGUARD_DATA_ROOT`). Consequences shipped with this change: `ChainedAuditLog` re-reads its
   head inside every append and retries lost PK races (never caches a head across appends), the
   mirror is compared seq-keyed (concurrent hosts interleave appends), and Server.Tests audit
   assertions are repo/agent-scoped — the same discipline the suite already used for queue rows.
