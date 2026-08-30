# M6: the card stops being told what the human had already approved.
from _lib import swap
swap("                PreviousScope: approved.Plan.Scope.ToList(),", "                PreviousScope: null,")
