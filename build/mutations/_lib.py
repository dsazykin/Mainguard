"""Shared helper for the re-scope mutation scripts.

Each script reads the pristine source on stdin and writes ONE broken version on stdout. `swap` fails
loudly when its anchor is absent, so a mutation that silently stopped applying — the quiet way a
mutation log becomes fiction — is an error rather than a green run.
"""
import sys


def swap(old, new):
    src = sys.stdin.read()
    if src.count(old) != 1:
        sys.stderr.write("anchor not found exactly once: %r\n" % old[:80])
        raise SystemExit(3)
    sys.stdout.write(src.replace(old, new, 1))
