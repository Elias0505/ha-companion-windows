// SPDX-License-Identifier: AGPL-3.0-only
//
// Visual + accessibility sweep for the site. Serves site/ from a throwaway
// static server, then walks every viewport, both themes and both motion
// preferences, runs axe on the states that usually break (each tab, the lightbox
// open), and fails on console errors or any request leaving the origin.
//
//   node tools/site/shots.mjs [--out /tmp/shots] [--file-protocol]
//
// Screenshots are written for eyeballing; they are never committed.

import { createServer } from "node:http";
import { readFile, mkdir, rm } from "node:fs/promises";
import { existsSync } from "node:fs";
import { extname, join, resolve, dirname } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { chromium } from "playwright";
import AxeBuilder from "@axe-core/playwright";

const here = dirname(fileURLToPath(import.meta.url));
const siteDir = resolve(here, "../../site");
const args = process.argv.slice(2);
const outDir = args.includes("--out") ? args[args.indexOf("--out") + 1] : "/tmp/shots";
const fileProtocol = args.includes("--file-protocol");

const MIME = {
  ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8", ".json": "application/json",
  ".webmanifest": "application/manifest+json", ".svg": "image/svg+xml",
  ".png": "image/png", ".jpg": "image/jpeg", ".webp": "image/webp",
  ".avif": "image/avif", ".ico": "image/x-icon", ".woff2": "font/woff2",
  ".mp4": "video/mp4", ".webm": "video/webm", ".xml": "application/xml",
  ".txt": "text/plain; charset=utf-8",
};

function startServer(root) {
  const server = createServer(async (req, res) => {
    try {
      const url = decodeURIComponent(req.url.split("?")[0]);
      let file = join(root, url === "/" ? "/index.html" : url);
      if (!file.startsWith(root)) { res.writeHead(403).end(); return; }
      if (!existsSync(file)) { file = join(root, "404.html"); res.statusCode = 404; }
      const body = await readFile(file);
      res.setHeader("content-type", MIME[extname(file)] || "application/octet-stream");
      res.end(body);
    } catch {
      res.writeHead(500).end();
    }
  });
  return new Promise((ok) => server.listen(0, "127.0.0.1", () => ok(server)));
}

const problems = [];
const note = (m) => { problems.push(m); console.log("  !! " + m); };

const server = fileProtocol ? null : await startServer(siteDir);
const base = fileProtocol
  ? pathToFileURL(join(siteDir, "index.html")).href
  : `http://127.0.0.1:${server.address().port}/`;

await rm(outDir, { recursive: true, force: true });
await mkdir(outDir, { recursive: true });

const browser = await chromium.launch({ args: ["--no-sandbox"] });

async function open({ width, height = 900, colorScheme = "dark", reducedMotion = "no-preference" }) {
  const context = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    colorScheme,
    reducedMotion,
  });
  const page = await context.newPage();

  // Under file:// Chromium CORS-blocks webfont loads and axe's internal CSS
  // XHR by policy — browser behaviour, not site bugs (the font stack has a
  // metric-matched fallback for exactly this). Filter that noise; everything
  // else still fails the run.
  const fileCorsNoise = (text) =>
    fileProtocol && /CORS policy|Failed to load resource|net::ERR_FAILED/.test(text);
  page.on("console", (msg) => {
    if (msg.type() === "error" && !fileCorsNoise(msg.text()))
      note(`console error @${width}/${colorScheme}: ${msg.text()}`);
  });
  page.on("pageerror", (err) => note(`page error @${width}/${colorScheme}: ${err.message}`));
  page.on("requestfailed", (req) => {
    const why = req.failure()?.errorText || "";
    if (/ERR_ABORTED/.test(why)) return;
    if (fileProtocol && req.url().startsWith("file://")) return;
    note(`request failed: ${req.url()} (${why})`);
  });
  page.on("request", (req) => {
    const url = req.url();
    if (url.startsWith("data:") || url.startsWith("blob:")) return;
    const sameOrigin = fileProtocol ? url.startsWith("file://") : url.startsWith(base);
    if (!sameOrigin) note(`third-party request: ${url}`);
  });

  await page.goto(base, { waitUntil: "load" });
  await page.waitForTimeout(1000);   // entrance settles
  // Full-page capture never scrolls, so the IntersectionObserver would leave
  // every .reveal transparent - and axe skips invisible elements, silently
  // exempting the lower half of the page. Force them in like a scroll would.
  await page.evaluate(() => {
    document.querySelectorAll(".reveal").forEach((el) => el.classList.add("is-in"));
  });
  await page.waitForTimeout(650);    // reveal transition settles
  return { context, page };
}

async function axeCheck(page, label) {
  if (fileProtocol) return;   // axe preloads CSSOM via XHR, which file:// forbids
  const res = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"])
    .analyze();
  if (res.violations.length) {
    for (const v of res.violations) {
      note(`axe [${label}] ${v.id} (${v.impact}) x${v.nodes.length}: ${v.help}`);
      for (const n of v.nodes.slice(0, 3)) {
        console.log("       " + n.target.join(" "));
        const data = (n.any || []).map((c) => c.data).filter(Boolean);
        if (data.length) console.log("         " + JSON.stringify(data[0]));
      }
    }
  } else {
    console.log(`  axe ${label}: clean`);
  }
}

console.log(`serving ${siteDir} at ${base}`);

for (const colorScheme of ["dark", "light"]) {
  for (const width of [360, 768, 1440, 2560]) {
    const { context, page } = await open({ width, colorScheme });
    await page.screenshot({ path: join(outDir, `page-${width}-${colorScheme}.png`), fullPage: true });
    console.log(`  shot ${width} ${colorScheme}`);
    if (width === 360 || width === 1440) await axeCheck(page, `${width}/${colorScheme}`);
    await context.close();
  }
}

// reduced motion, one width per theme — everything must be visible immediately
for (const colorScheme of ["dark", "light"]) {
  const { context, page } = await open({ width: 1440, colorScheme, reducedMotion: "reduce" });
  await page.screenshot({ path: join(outDir, `page-1440-${colorScheme}-reduced.png`), fullPage: true });
  const hidden = await page.evaluate(() =>
    [...document.querySelectorAll("[data-hero], .reveal")]
      .filter((el) => Number(getComputedStyle(el).opacity) < 0.99).length);
  if (hidden) note(`reduced motion (${colorScheme}): ${hidden} element(s) still transparent`);
  await context.close();
}

// every tab of the tour, then lightbox and copy button
{
  const { context, page } = await open({ width: 1440 });
  const tabs = await page.locator('#tour [role="tab"]').all();
  for (const tab of tabs) {
    const id = await tab.getAttribute("id");
    await tab.click();
    await page.waitForTimeout(420);
    await page.locator("#tour").screenshot({ path: join(outDir, `tour-${id}.png`) });
    await axeCheck(page, `tour:${id}`);
  }

  // lightbox: opens, traps focus, closes, returns focus
  await page.locator('#tour .panel.is-active [data-lightbox]').first().click();
  await page.waitForTimeout(400);
  const dialogOpen = await page.evaluate(() => document.querySelector("dialog.lightbox").open);
  if (!dialogOpen) note("lightbox did not open");
  await page.screenshot({ path: join(outDir, "lightbox.png") });
  await axeCheck(page, "lightbox");
  await page.keyboard.press("Escape");
  await page.waitForTimeout(300);

  // copy button must not claim success it did not get
  await page.locator('#install [data-copy]').first().click();
  await page.waitForTimeout(250);
  const label = await page.locator('#install [data-copy] .copy__label').first().textContent();
  console.log(`  copy button says: "${label.trim()}"`);

  await context.close();
}

// section close-ups for design review (viewport-sized, both themes)
for (const colorScheme of ["dark", "light"]) {
  const { context, page } = await open({ width: 1440, height: 1000, colorScheme });
  for (const id of ["top", "features", "tour", "two-way", "install", "open-source", "faq"]) {
    const el = page.locator("#" + id);
    if (await el.count()) {
      await el.scrollIntoViewIfNeeded();
      await page.waitForTimeout(650);
      await el.screenshot({ path: join(outDir, `sec-${id}-${colorScheme}.png`) });
    }
  }
  await context.close();
}

// keyboard order: the first 25 stops, as accessible names
{
  const { context, page } = await open({ width: 1440 });
  const order = [];
  for (let i = 0; i < 25; i++) {
    await page.keyboard.press("Tab");
    order.push(await page.evaluate(() => {
      const el = document.activeElement;
      if (!el || el === document.body) return "(body)";
      const name = (el.getAttribute("aria-label") || el.textContent || "").trim().replace(/\s+/g, " ").slice(0, 40);
      return `${el.tagName.toLowerCase()}: ${name}`;
    }));
  }
  console.log("\n  tab order:");
  order.forEach((o, i) => console.log(`   ${String(i + 1).padStart(2)}. ${o}`));
  await context.close();
}

await browser.close();
if (server) server.close();

console.log(`\nscreenshots: ${outDir}`);
if (problems.length) {
  console.log(`\nFAILED with ${problems.length} problem(s).`);
  process.exit(1);
}
console.log("\nclean: no console errors, no third-party requests, no axe violations.");
