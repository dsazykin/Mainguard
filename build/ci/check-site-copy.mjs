#!/usr/bin/env node
/**
 * Guards the voice of the marketing copy.
 *
 * Why this exists: the site's copy was rewritten once to strip em dashes and
 * the stock phrasing that makes writing read as machine-made. Nothing stopped
 * the next edit from putting it all back, and copy drift is invisible to a
 * type checker and to every other guard in this job.
 *
 * Two rules, both only on text that actually renders:
 *
 *   1. No em dash outside the legal pages. An em dash is usually holding
 *      together a thought that wants to be two sentences, so the fix is to
 *      write two sentences, not to swap in a comma.
 *   2. No stock phrasing from the list below.
 *
 * /privacy and /terms are exempt on purpose. Their register is different, a
 * long qualifying clause earns its keep there, and their wording was chosen
 * against the regulations rather than for rhythm.
 *
 * Comments are stripped before scanning, so a note explaining a rule cannot
 * trip the rule. Run from the repository root:
 *
 *   node build/ci/check-site-copy.mjs
 */
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const ROOT = 'site/src';
const EXTRA = ['site/index.html'];
/** Basenames whose register is legal rather than marketing. */
const EXEMPT = ['Privacy.tsx', 'Terms.tsx'];

/** [pattern, what it is] — kept short, and only things that are never right here. */
const STOCK_PHRASING = [
  [/\bit'?s not (just )?(about )?\w+,? it'?s\b/i, '"it\'s not X, it\'s Y"'],
  [/\bnot only\b.{0,40}\bbut also\b/i, '"not only ... but also"'],
  [/\bwhether you'?re\b/i, '"whether you\'re X or Y"'],
  [
    /\b(seamless|seamlessly|effortless|effortlessly|robust|leverage the|elevate|unlock the|empower|delve|realm of|testament to|tapestry|embark|holistic|synerg)/i,
    'marketing filler',
  ],
  [/\bin today'?s .{0,25}(world|landscape|era)\b/i, '"in today\'s landscape"'],
  [/\bat the end of the day\b/i, '"at the end of the day"'],
  [/\bhere'?s the thing\b/i, '"here\'s the thing"'],
  [/\bthat'?s the (point|promise|whole point)\b/i, 'rhetorical kicker'],
  [/\bgame[- ]chang/i, '"game-changing"'],
  [/\bcutting[- ]edge\b/i, '"cutting-edge"'],
  [/\bsupercharge/i, '"supercharge"'],
  [/\bdive (deep )?into\b/i, '"dive into"'],
  [/\bit'?s worth noting\b/i, '"it\'s worth noting"'],
  [/\b(pivotal|paramount)\b/i, '"pivotal"/"paramount"'],
  [/\b(moreover|furthermore),/i, '"moreover"/"furthermore"'],
  [/\bever[- ]evolving|fast[- ]paced\b/i, '"ever-evolving"/"fast-paced"'],
  [/\b(unparalleled|unrivall?ed)\b/i, '"unparalleled"'],
  [/\b(plethora|myriad)\b/i, '"plethora"/"myriad"'],
];

function walk(p, out = []) {
  if (!existsSync(p)) return out;
  if (statSync(p).isDirectory()) for (const n of readdirSync(p)) walk(join(p, n), out);
  else if (/\.(tsx?|html)$/.test(p)) out.push(p);
  return out;
}

/**
 * Remove anything that cannot reach the page: block comments, JSX comments,
 * HTML comments and line comments. The `[^:]` guard keeps `https://` intact.
 */
function stripComments(src) {
  return src
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\{\s*\/\*[\s\S]*?\*\/\s*\}/g, '')
    .replace(/<!--[\s\S]*?-->/g, '')
    .split('\n')
    .map((l) => l.replace(/(^|[^:])\/\/.*$/, '$1'))
    .join('\n');
}

const files = walk(ROOT).concat(EXTRA.filter((f) => existsSync(f)));
const scanned = files.filter((f) => !EXEMPT.some((e) => f.endsWith(e)));

// Same paranoia as the conflict-marker step: a guard that scans nothing passes
// every time, which is worse than no guard at all.
if (scanned.length < 10) {
  console.error(`::error::check-site-copy scanned only ${scanned.length} files — vacuous scan`);
  process.exit(1);
}
if (!files.some((f) => EXEMPT.some((e) => f.endsWith(e)))) {
  console.error('::error::the exempt legal pages were not found — has the site layout moved?');
  process.exit(1);
}

let failures = 0;
const report = (file, line, text, why) => {
  failures++;
  console.error(`::error file=${file},line=${line}::${why}: ${text.trim().slice(0, 160)}`);
};

for (const file of scanned) {
  stripComments(readFileSync(file, 'utf8'))
    .split('\n')
    .forEach((line, i) => {
      if (line.includes('—')) report(file, i + 1, line, 'em dash in rendered copy');
      for (const [re, what] of STOCK_PHRASING) {
        if (re.test(line)) report(file, i + 1, line, `stock phrasing ${what}`);
      }
    });
}

console.log(`check-site-copy: scanned ${scanned.length} files (${files.length - scanned.length} legal pages exempt)`);
if (failures) {
  console.error(
    `\n${failures} problem(s). Rewrite the sentence rather than swapping the punctuation. ` +
      'If a new phrase is a genuine false positive, narrow the pattern in build/ci/check-site-copy.mjs.',
  );
  process.exit(1);
}
console.log('check-site-copy: copy is clean.');
