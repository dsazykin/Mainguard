# M5: "the approved plan" becomes the newest-filtered read again — the F6 hole a pending re-scope opens.
from _lib import swap
swap("    public TaskPlan? ApprovedPlanFor(string workerAgentId) => ApprovedForWorker(workerAgentId)?.Plan;",
     "    public TaskPlan? ApprovedPlanFor(string workerAgentId) =>\n"
     "        LatestForWorker(workerAgentId) is { Status: PlanStatus.Approved } p ? p.Plan : null;")
