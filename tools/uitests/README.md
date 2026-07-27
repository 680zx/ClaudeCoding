# UI tests

Drives the running app in a real browser. These are the checks behind the
phase 1 and phase 3 UI proofs — a screenshot of a chart that rendered is worth
more than an assertion that it should have.

Playwright lives here rather than in `Parallels.Web` deliberately: its
postinstall downloads browser binaries, which breaks the frontend's own
container (`node:22-alpine` is musl-based) and bloats it regardless. The SPA
should not carry a test-only native dependency.

## Running

Needs the API and the Vite dev server already up.

```bash
npm install
CHROMIUM=/path/to/chromium node backtest-tab.mjs   # run, stats, chart
node save-modal.mjs                                # Save modal persistence
node live-tab.mjs                                  # Live tab tile CRUD
```

`CHROMIUM` overrides the browser path; it defaults to Playwright's own
download, and falls back to a preinstalled Chromium when one is present.
