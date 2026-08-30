using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <summary>
/// K3 — adds the branch-side half of the identity a merge lease is granted for: the
/// <c>agent/&lt;id&gt;</c> tip the queue's verification was measured on. The lease already pinned the
/// <c>main@sha</c> the merge could fast-forward, so it could say which main was authorized and
/// structurally could not say which COMMITS were — which left the lease a mutex over a repository rather
/// than a claim about a merge.
///
/// <para>Existing rows take <c>""</c>, the same choice <c>AddVerificationBranchSha</c> made and for the
/// same reason: nothing knows what tip they were granted against, and every comparison reads empty as
/// "not measured" and declines to answer rather than manufacturing a refusal.</para>
/// </summary>
public partial class AddMergeLeaseExpectedBranchSha : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExpectedBranchSha",
            table: "MergeLeaseRows",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExpectedBranchSha",
            table: "MergeLeaseRows");
    }
}
