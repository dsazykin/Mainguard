# M4: approving a re-scope stops retiring the plan it widens — two approved plans for one worker.
from _lib import swap
swap("""            if (approved.IsRescope &&
                _plans.TryGetValue(approved.SupersedesPlanId!, out var previous) &&
                previous.Status == PlanStatus.Approved)""",
     """            if (false &&
                _plans.TryGetValue(approved.SupersedesPlanId ?? "", out var previous) &&
                previous.Status == PlanStatus.Approved)""")
