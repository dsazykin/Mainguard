# M1: Rescope stops requiring an APPROVED plan.
from _lib import swap
swap("            if (approved.Status != PlanStatus.Approved)",
     "            if (false)")
