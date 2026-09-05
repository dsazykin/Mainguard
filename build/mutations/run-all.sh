#!/usr/bin/env bash
# Every guard the re-scope op rests on, broken one at a time. See ../mutate.sh for why each restore
# is followed by a `touch` and why a run whose assembly did not rebuild refuses to report.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
M="$HERE/../mutate.sh"

PLANS=Mainguard.Agents/Agents/Orchestrator/PlanApprovalService.cs
SHIM=Mainguard.Agents/Agents/Ipc/WorkerPlanShim.cs
INSTR=Mainguard.Agents/Agents/Ipc/AgentOperatingInstructions.cs
CARD=Mainguard.Agents.UI/ViewModels/CoordinatorPanelViewModel.cs
SPAWN=Mainguard.Server/Runtime/AgentSpawnService.cs

run() { "$M" "$@" || echo "!! $1 did not behave"; }

run m1  "$PLANS" build/mutations/m1_rescope_accepts_any_status.py   Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m2  "$PLANS" build/mutations/m2_escalation_not_terminal.py      Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m3  "$PLANS" build/mutations/m3_many_live_rescopes.py           Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m4  "$PLANS" build/mutations/m4_no_supersede.py                 Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m5  "$PLANS" build/mutations/m5_authorisation_is_newest.py      Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m6  "$PLANS" build/mutations/m6_previous_scope_not_copied.py    Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m7  "$PLANS" build/mutations/m7_present_loses_the_hint.py       Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m8  "$PLANS" build/mutations/m8_revise_loses_the_hint.py        Mainguard.Tests        "FullyQualifiedName~WorkerRescopeTests"
run m9  "$SHIM"  build/mutations/m9_shim_drops_the_id_guard.py      Mainguard.Tests        "FullyQualifiedName~AgentIpcProtocolTests"
run m10 "$INSTR" build/mutations/m10_instructions_drop_rescope.py   Mainguard.Tests        "FullyQualifiedName~AgentOperatingInstructionsTests"
run m11 "$SHIM"  build/mutations/m11_verbs_drop_rescope.py          Mainguard.Tests        "FullyQualifiedName~AgentOperatingInstructionsTests"
run m12 "$SPAWN" build/mutations/m12_decision_drops_rescope_of.py   Mainguard.Server.Tests "FullyQualifiedName~WorkerPlanChannelIpcTests"
run m13 "$SPAWN" build/mutations/m13_handler_skips_ownership.py     Mainguard.Server.Tests "FullyQualifiedName~WorkerPlanChannelIpcTests"
run m14 "$SPAWN" build/mutations/m14_handler_infers_the_plan.py     Mainguard.Server.Tests "FullyQualifiedName~WorkerPlanChannelIpcTests"
run m15 "$SHIM"  build/mutations/m15_brief_hides_the_plan_id.py     Mainguard.Tests        "FullyQualifiedName~AgentIpcProtocolTests"
run m16 "$CARD"  build/mutations/m16_reject_button_ignores_rescope.py Mainguard.Tests      "FullyQualifiedName~CoordinatorPlanGateRenderHarness"
