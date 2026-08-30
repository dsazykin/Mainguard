using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <summary>
/// Adds the branch-side half of a verification's provenance: the <c>refs/heads/agent/&lt;id&gt;</c> tip the
/// run was measured ON. A record already pinned <c>main@sha</c>, so the queue could ask whether main had
/// moved under its evidence and structurally could not ask whether the BRANCH had — which is how a green
/// row survived three further commits and kept offering Merge.
///
/// <para>Existing rows take <c>""</c>, and that is the correct value rather than a backfill: nothing knows
/// what tip they ran on. Empty is read as "not measured", and every freshness comparison declines to
/// answer on it instead of manufacturing a fresh verdict.</para>
/// </summary>
public partial class AddVerificationBranchSha : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BranchSha",
            table: "VerificationRows",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BranchSha",
            table: "VerificationRows");
    }
}
