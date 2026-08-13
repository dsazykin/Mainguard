#!/usr/bin/env python3
"""Assert every SVG under site/ is well-formed XML.

Ticket #57: deploy-site.yml only ran on `push` to main, so nothing under site/ was validated by PR CI.
#310 committed 11 lines of `git stash pop` conflict markers into site/public/favicon.svg and kept a
green CI — the markers make the file malformed XML (a parser fails at line 4), but no build step reads
an SVG in public/, so `npm run build` succeeds and ships it.

Exits non-zero if any SVG fails to parse, or if it found none to parse (a vacuous run is a failure).
"""
import pathlib
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2] / "site"
SKIP = {"node_modules", "dist"}

svgs = [p for p in ROOT.rglob("*.svg") if SKIP.isdisjoint(p.parts)]
if not svgs:
    sys.exit(f"::error::found no SVGs under {ROOT} — this check would be vacuous")

bad = []
for p in svgs:
    try:
        ET.parse(p)
    except ET.ParseError as e:
        bad.append((p, e))

for p, e in bad:
    print(f"::error file={p}::malformed SVG: {e}")

print(f"parsed {len(svgs)} SVGs under site/, {len(bad)} malformed")
sys.exit(1 if bad else 0)
