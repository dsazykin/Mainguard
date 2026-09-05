# M9: the shim stops refusing an id-less rescope and builds a request naming nothing.
import sys

src = sys.stdin.read()
old = """        if len(argv) < 4:
            sys.stderr.write("""
new = """        if False:
            sys.stderr.write("""
assert src.count(old) == 1, "anchor not found exactly once"
sys.stdout.write(src.replace(old, new, 1))
