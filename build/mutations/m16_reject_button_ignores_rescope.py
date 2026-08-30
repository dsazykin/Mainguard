# M16: the card's Reject button stops distinguishing a re-scope, so declining a widening claims to stop
# the worker -- a false consequence, and the kind that pressures a human into approving.
from _lib import swap
swap('''        RejectButtonText = IsRescope
            ? "Decline the widening"
            : NextRejectionEscalates ? "Reject — worker will stop" : "Reject with feedback";''',
     '''        RejectButtonText =
            NextRejectionEscalates ? "Reject — worker will stop" : "Reject with feedback";''')
