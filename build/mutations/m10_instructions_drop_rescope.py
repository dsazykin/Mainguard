# M10: the worker is no longer taught the op the daemon serves it.
import sys
src = sys.stdin.read()
start = src.index("        ## If the work needs a file your approved scope does not cover, ASK")
end = src.index("        ## When the work is done, commit it")
sys.stdout.write(src[:start] + src[end:])
