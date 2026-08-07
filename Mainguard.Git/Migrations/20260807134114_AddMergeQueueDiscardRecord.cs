using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddMergeQueueDiscardRecord : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DiscardReason",
            table: "MergeQueueRows",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "DiscardedAtUtc",
            table: "MergeQueueRows",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DiscardedBy",
            table: "MergeQueueRows",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DiscardReason",
            table: "MergeQueueRows");

        migrationBuilder.DropColumn(
            name: "DiscardedAtUtc",
            table: "MergeQueueRows");

        migrationBuilder.DropColumn(
            name: "DiscardedBy",
            table: "MergeQueueRows");
    }
}
