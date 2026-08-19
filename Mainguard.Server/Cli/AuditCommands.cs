using System;
using System.IO;
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
            var log = new ChainedAuditLog(
                () => new AppDbContext(dbPath),
                new AuditCrypto(new SecureKeyring(keyringDir)),
                new AuditFileMirror(dbPath + ".audit-mirror"));

            var (valid, firstBadSeq) = log.VerifyAll();
            var head = log.Head();
            Console.WriteLine($"audit verify: {dbPath}");
            Console.WriteLine(head is null
                ? $"chain: empty · head: {HashChain.GenesisHash}"
                : $"chain: {head.Value.Seq} record(s) · head: {head.Value.Hash}");

            if (valid)
            {
                Console.WriteLine("OK");
                return 0;
            }

            Console.WriteLine($"TAMPERED — first bad seq: {firstBadSeq}");
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
