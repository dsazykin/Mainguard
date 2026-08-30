# M11: the op/verb map loses the entry, so the instructions pin has nothing to check against.
from _lib import swap
swap('            [AgentIpcRequest.RescopePlanOp] = "rescope",\n', "")
