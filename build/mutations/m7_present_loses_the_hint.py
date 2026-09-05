# M7: the dead-end refusal stops naming the way out (the defect, restored).
from _lib import swap
swap("""                        PlanStatus.Approved =>
                            $"Plan '{live.PlanId}' is already approved for this worker. To change what it "
                            + "authorises — a file the approved scope does not cover — re-scope it: "
                            + "mainguard-plan " + Mainguard.Agents.Agents.Ipc.WorkerPlanShim.RescopeUsage,""",
     """                        PlanStatus.Approved =>
                            $"Plan '{live.PlanId}' is already approved for this worker.",""")
