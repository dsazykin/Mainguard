# M8: revise's refusal stops naming rescope, so a mis-picked verb is a wall rather than a correction.
from _lib import swap
swap("""                    plan.Status == PlanStatus.Approved
                        ? $"Plan '{planId}' is Approved — only a rejected plan can be revised. To widen "
                          + "what an approved plan authorises, re-scope it: mainguard-plan "
                          + Mainguard.Agents.Agents.Ipc.WorkerPlanShim.RescopeUsage
                        : $"Plan '{planId}' is {plan.Status} — only a rejected plan can be revised.",""",
     """                    $"Plan '{planId}' is {plan.Status} — only a rejected plan can be revised.",""")
