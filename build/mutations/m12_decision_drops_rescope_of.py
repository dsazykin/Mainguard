# M12: the decision stops saying it was a widening, so a refused re-scope reads as a full stop.
from _lib import swap
swap("            RescopeOf: plan?.SupersedesPlanId);", "            RescopeOf: null);")
