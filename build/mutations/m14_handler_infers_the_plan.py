# M14: a rescope naming no plan is guessed at instead of refused.
import sys
src = sys.stdin.read()
old = """        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            return new AgentIpcResponse(
                Ok: false,
                Error: "the approved plan's id is required (mainguard-plan "
                       + WorkerPlanShim.RescopeUsage + ")");
        }
"""
new = """        request = request with { PlanId = request.PlanId is { Length: > 0 } id
            ? id
            : _plans.ApprovedForWorker(workerAgentId)?.PlanId ?? "" };
"""
assert src.count(old) == 1, "anchor not found exactly once"
sys.stdout.write(src.replace(old, new, 1))
