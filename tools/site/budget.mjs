// SPDX-License-Identifier: AGPL-3.0-only
//
// Byte-budget gate for the website. Reads tools/site/budget.json, prints a
// table of file | raw | gzip | budget | headroom and exits 1 on any overflow —
// including the two aggregate first-view budgets and the "no GIF ever ships"
// rule. Run before every push that touches site/.
//
//   node tools/site/budget.mjs

import { readFile, readdir, stat } from "node:fs/promises";
import { join, resolve, dirname, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "../../site");
const config = JSON.parse(await readFile(join(here, "budget.json"), "utf8"));

let failed = false;
const fail = (msg) => { failed = true; console.log("  FAIL " + msg); };

async function walk(dir) {
  const out = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const p = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...await walk(p));
    else out.push(p);
  }
  return out;
}

const files = await walk(root);

// -- rule 1: no GIF below site/, ever (the repo's GIFs stay in docs/media) ----
for (const f of files) {
  if (f.toLowerCase().endsWith(".gif")) fail(`GIF shipped: ${relative(root, f)}`);
}

// -- rule 2: per-file budgets (gzip for text, raw for binary) -----------------
const TEXT = /\.(html|css|js|mjs|json|webmanifest|svg|txt|xml)$/i;
async function measure(rel) {
  const p = join(root, rel);
  const raw = (await stat(p)).size;
  const gz = TEXT.test(rel) ? gzipSync(await readFile(p), { level: 9 }).length : raw;
  return { raw, gz };
}

console.log("file".padEnd(46) + "raw".padStart(9) + "gzip".padStart(9) + "budget".padStart(9) + "  left");
const measured = {};
for (const [rel, budget] of Object.entries(config.files)) {
  let m;
  try {
    m = await measure(rel);
  } catch {
    fail(`missing file in budget list: ${rel}`);
    continue;
  }
  measured[rel] = m;
  const used = TEXT.test(rel) ? m.gz : m.raw;
  const left = budget - used;
  console.log(
    rel.padEnd(46) +
    String(m.raw).padStart(9) +
    (TEXT.test(rel) ? String(m.gz) : "-").padStart(9) +
    String(budget).padStart(9) +
    String(left).padStart(7)
  );
  if (used > budget) fail(`${rel}: ${used} B > budget ${budget} B`);
}

// -- rule 3: aggregate first-view sets ---------------------------------------
for (const [name, set] of Object.entries(config.aggregates)) {
  let sum = 0;
  for (const rel of set.files) {
    const m = measured[rel] ?? await measure(rel);
    sum += TEXT.test(rel) ? m.gz : m.raw;
  }
  const ok = sum <= set.budget;
  console.log(`\n${name}: ${sum} B of ${set.budget} B ${ok ? "(ok)" : ""}`);
  if (!ok) fail(`${name}: ${sum} B > ${set.budget} B`);
}

console.log(failed ? "\nbudget: FAILED" : "\nbudget: ok");
process.exit(failed ? 1 : 0);
