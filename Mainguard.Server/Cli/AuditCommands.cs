using System;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Security;

namespace Mainguard.Server.Cli;

/// <summary>
/// P2-15: the <c>mainguardd audit verify</c> CLI verb — walks the whole chain (DB + file-mirror
/// witness) offline, no running daemon needed, and prints the head so an operator can note it down
/// out-of-band. Exit-code contract (spec): <c>0</c> chain intact (a fresh install with no store yet
/// is intact by definition), <c>2</c> tampered — first bad seq printed, <c>64</c> usage,
/// <c>1</c> unexpected failure (unreadable DB, missing key, …) — which is "cannot verify",
/// deliberately distinct from "verified".
/// </summary>
public static class AuditCommands
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[1], "verify", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: mainguardd audit verify [--data <daemon-db-path>]");
            return 64;
        }

        string? explicitPath = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--data")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--data requires the daemon SQLite path.");
                    return 64;
                }

                explicitPath = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"unknown argument '{args[i]}'.");
                return 64;
            }
        }

        var dbPath = explicitPath ?? Path.Combine(MainguardPaths.DataRoot(), "mainguard-daemon.db");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"audit verify: no audit store at {dbPath}");
            Console.WriteLine($"chain: empty · head: {HashChain.GenesisHash}");
            Console.WriteLine("OK");
            return 0;
        }

        try
        {
            var directory = Path.GetDirectoryName(dbPath);
            var keyringDir = string.IsNullOrEmpty(directory) ? "audit-keyring" : Path.Combine(directory, "audit-keyring");

            // READ-ONLY: verify never mints a master key. The minting constructor made this command a
            // writer — on a box with no key-ring protector it exited 1 with "Refusing to store
            // 'audit-payload-key'…" where it used to report an intact empty chain, and on every other
            // box it left a key behind that the operator never asked for.
            // Not even the key-ring DIRECTORY is created here: a verify run on a box that has never
            // run the daemon should leave the filesystem exactly as it found it.
            var crypto = Directory.Exists(keyringDir)
                ? AuditCrypto.TryOpenExisting(new SecureKeyring(keyringDir))
                : null;
            if (crypto is null)
            {
                // No master key means no encrypted payload can exist. An empty chain is intact by
                // definition; a non-empty one cannot be verified, and that is not the same as OK.
                using var probe = new AppDbContext(dbPath);
                if (probe.AuditRecords.Any())
                {
                    Console.Error.WriteLine(
                        $"audit verify failed: {dbPath} holds audit records but the master key "
                        + $"('{AuditCrypto.KeyName}') is not in the key ring at {keyringDir} — the chain "
                        + "cannot be decrypted, so it cannot be verified.");
                    return 1;
                }

                Console.WriteLine($"audit verify: {dbPath}");
                Console.WriteLine($"chain: empty · head: {HashChain.GenesisHash}");
                Console.WriteLine("OK");
                return 0;
            }

            var log = new ChainedAuditLog(
                () => new AppDbContext(dbPath),
                crypto,
                new AuditFileMirror(dbPath + ".audit-mirror"));

            var (valid, firstBadSeq) = log.VerifyAll();
            var head = log.Head();
            Console.WriteLine($"audit verify: {dbPath}");
            Console.WriteLine(head is null
                ? $"chain: empty · head: {HashChain.GenesisHash}"
                : $"chain: {head.Value.Seq} record(s) · head: {head.Value.Hash}");

            // Anchor tokens, when present, are validated structurally (spec step 5: the walk
            // "+ validates anchor tokens"). A stored token that no longer matches its recorded
            // head hash is verification failure, same exit as chain tamper.
            var badAnchors = new AuditAnchorQueue(() => new AppDbContext(dbPath)).ValidateStoredAnchors();
            if (badAnchors.Count > 0)
            {
                Console.WriteLine($"ANCHOR INVALID — id(s): {string.Join(", ", badAnchors)}");
            }

            if (valid && badAnchors.Count == 0)
            {
                Console.WriteLine("OK");
                return 0;
            }

            if (!valid)
            {
                Console.WriteLine($"TAMPERED — first bad seq: {firstBadSeq}");
            }

            return 2;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // A pre-P2-15 daemon DB: the store exists, the chain does not — intact by definition.
            Console.WriteLine($"audit verify: {dbPath} predates the audit chain (no AuditRecords table)");
            Console.WriteLine($"chain: empty · head: {HashChain.GenesisHash}");
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"audit verify failed: {ex.Message}");
            return 1;
        }
    }
}
