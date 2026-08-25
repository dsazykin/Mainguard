using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampText = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadCiphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    KeyId = table.Column<string>(type: "TEXT", nullable: true),
                    PrevHash = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Redacted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Seq);
                });

            // P2-15: append-only AT THE SCHEMA LEVEL, not just by API shape. Any DELETE aborts;
            // the ONLY legal UPDATE is the redaction tombstone transition — chain columns
            // byte-identical, Redacted 0→1, payload + key destroyed. Even raw SQL against the file
            // must first DROP these triggers to rewrite history, and the hash chain catches that.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER AuditRecords_no_delete
                BEFORE DELETE ON AuditRecords
                BEGIN
                    SELECT RAISE(ABORT, 'audit records are append-only (P2-15): DELETE is never legal');
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER AuditRecords_no_update
                BEFORE UPDATE ON AuditRecords
                WHEN NEW.Seq != OLD.Seq
                    OR NEW.TimestampText != OLD.TimestampText
                    OR NEW.Type != OLD.Type
                    OR NEW.PrevHash != OLD.PrevHash
                    OR NEW.Hash != OLD.Hash
                    OR NOT (OLD.Redacted = 0 AND NEW.Redacted = 1)
                    OR NEW.PayloadCiphertext IS NOT NULL
                    OR NEW.KeyId IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'audit records are append-only (P2-15): only the redaction tombstone transition is legal');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditRecords");
        }
    }
}
