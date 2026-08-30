# M2: an escalated re-scope stops being terminal — the reject -> re-scope -> reject loop reopens.
from _lib import swap
swap("            if (escalatedRescope is not null)", "            if (false)")
