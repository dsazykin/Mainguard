# M15: `brief` stops printing the live plan id, so the id-less rescope refusal's advice stops working.
from _lib import swap
swap('''            if response.get("planId"):
                print("PLAN: %s (%s)" % (response["planId"], response.get("status") or "unknown"))
''', "")
