using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddAuditAnchors : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditAnchors",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                HeadSeq = table.Column<long>(type: "INTEGER", nullable: false),
                HeadHash = table.Column<string>(type: "TEXT", nullable: false),
                RequestedAtText = table.Column<string>(type: "TEXT", nullable: false),
                Token = table.Column<byte[]>(type: "BLOB", nullable: true),
                AnchoredAtText = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditAnchors", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditAnchors_HeadSeq",
            table: "AuditAnchors",
            column: "HeadSeq",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditAnchors");
    }
}
