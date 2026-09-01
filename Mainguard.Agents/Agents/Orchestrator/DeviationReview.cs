using System;
using System.Collections.Generic;
using Mainguard.Git.Review;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// Turns a worker's commit-time <see cref="DeviationDeclaration"/> into must-acknowledge review rows —
/// the second half of the approved-plan gate, the half that is about the APPROACH rather than the scope.
///
/// <para><b>The defect this closes.</b> A worker's approved plan said, in its <c>approach</c>, that the
/// module had no validation idiom and it would keep plain <c>a / b</c>. It shipped a <c>RangeError</c> on
/// zero plus a <c>checkOperands</c>/<c>checkResult</c> layer that changed the behaviour of three
/// pre-existing functions. Every control held and none of them was looking: the file scope was honoured
/// so <c>FlaggedItems</c> was empty, and verification was green because the worker had also written the
/// tests asserting its own new behaviour. <b>A worker that owns its tests can always turn its divergence
/// green</b> — verification proves a diff is self-consistent, never that it matches what was approved.</para>
///
/// <para><b>What this is, and what it is deliberately not.</b> It is not a comparison: nothing here reads
/// the diff or the approach text, and no automated approach-vs-diff judgement was built (that was
/// considered and rejected). It is the worker's own declaration, promoted to a row a human must clear —
/// so the claim is attributable, is on the same screen as the approach it is about, and cannot be made by
/// saying nothing.</para>
///
/// <para><b>Pure, and daemon-armed.</b> No IO, no repo — like <see cref="FlaggedChangeDetector"/>. It is
/// called from <c>MergeQueueProvisioner.ArmFlaggedChangeReview</c>, i.e. into the
/// <see cref="AcknowledgmentStore"/> that <see cref="FlaggedChangeGate"/> actually reads, for the reason
/// <c>ReviewLockfiles</c> spells out: rows composed client-side render and block nothing, and an
/// acknowledgment addressed to a locally-minted id clears a store no merge consults.</para>
/// </summary>
public static class DeviationReview
{
    /// <summary>The path column for a declared-deviation row — a branch-level fact, not a file's.</summary>
    public const string DeclaredPath = "(worker-declared deviation)";

    /// <summary>The path column for the missing-declaration row.</summary>
    public const string MissingPath = "(deviation declaration)";

    /// <summary>
    /// The rows for one branch's declaration. <b>Three inputs, three outcomes</b>, and only the middle one
    /// produces nothing:
    /// <list type="bullet">
    /// <item><see cref="DeviationDeclaration.Declared"/> — one must-ack row per declared departure.</item>
    /// <item><see cref="DeviationDeclaration.None"/> — no row. The worker asserted it followed the
    /// approach; that assertion is rendered beside the approach itself (it is a claim to weigh, not a
    /// hazard to clear), and it is on the audit chain either way.</item>
    /// <item><see cref="DeviationDeclaration.NotDeclared"/> — one must-ack row saying so. Fail-closed:
    /// nobody answered, and an omitted item would read as "answered, and the answer was no".</item>
    /// </list>
    /// </summary>
    /// <param name="declaration">The worker's answer, as recorded on its approved plan.</param>
    /// <param name="deviations">The declared departure texts (ignored unless <paramref name="declaration"/>
    /// is <see cref="DeviationDeclaration.Declared"/>).</param>
    /// <param name="diffHash">
    /// <see cref="FlaggedChangeDetector.HashDiff"/> of the branch's merge diff. Folded into every row's
    /// content hash so invariant 2 holds for these rows the way it holds for per-file ones: a new push
    /// produces new ids and the acknowledgments reset. Without it a human's "yes, I read that deviation"
    /// would survive a push that rewrote the change it was granted for.
    /// </param>
    public static IReadOnlyList<FlaggedChange> ItemsFor(
        DeviationDeclaration declaration,
        IReadOnlyList<string>? deviations,
        string diffHash)
    {
        var items = new List<FlaggedChange>();
        switch (declaration)
        {
            case DeviationDeclaration.Declared:
                var index = 0;
                foreach (var text in deviations ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    items.Add(new FlaggedChange(
                        DeclaredPath,
                        // Source, not one of the four "mere presence is flag-worthy" categories: this row
                        // is flag-worthy because the WORKER said so, not because a path rule fired, and
                        // borrowing ExecutableConfig would put a hazard word on a hunk that may be none.
                        // Every item in this store is must-acknowledge regardless of category — the
                        // category is a render hint (FlaggedChangesPanelViewModel.KindOf reads the KIND
                        // out of the id, which is why the kind is what carries the meaning here).
                        RiskCategory.Source,
                        FlaggedKind.DeclaredDeviation,
                        AcknowledgmentStore.HashContent($"{diffHash}|deviation|{index}|{text.Trim()}"),
                        $"the worker declared it departed from the approved approach — \"{text.Trim()}\""));
                    index++;
                }

                break;

            case DeviationDeclaration.NotDeclared:
                items.Add(new FlaggedChange(
                    MissingPath,
                    RiskCategory.Source,
                    FlaggedKind.DeviationDeclarationMissing,
                    AcknowledgmentStore.HashContent($"{diffHash}|deviation-not-declared"),
                    "no deviation declaration is on record for this branch — nothing states whether the "
                    + "work follows the approved approach, so read the approach against the diff yourself"));
                break;

            case DeviationDeclaration.None:
            default:
                break;
        }

        return items;
    }
}
