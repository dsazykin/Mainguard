# M13: rescope stops checking plan ownership — the channel becomes an existence oracle.
import sys
src = sys.stdin.read()
old = """        if (!OwnsPlan(workerAgentId, request.PlanId, out var ownershipError))
        {
            return ownershipError;
        }

        if (!TryValidatePlan(request.PlanJson, out var fields, out var invalid))
        {
            return invalid;
        }

        var rescope = _plans.Rescope("""
new = """        if (!TryValidatePlan(request.PlanJson, out var fields, out var invalid))
        {
            return invalid;
        }

        var rescope = _plans.Rescope("""
assert src.count(old) == 1, "anchor not found exactly once"
sys.stdout.write(src.replace(old, new, 1))
