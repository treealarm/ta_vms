# `npm run lint` in web_srv.client is red, and was before M0

`npm run lint` (plain `eslint .`) exits 1 with 27 problems: 22 errors and 5 warnings,
spread over about fifteen components. Found while verifying M0 of the Square merge
(2026-10-30); none of them comes from a file M0 touched (the only client file it changed
is `MonitorsView.tsx`, which is clean), and the count matches what the M0 plan recorded
on 2026-10-28, so the debt predates the merge.

By rule:

| Rule | Count |
|---|---|
| `react-hooks/set-state-in-effect` | 16 |
| `react-hooks/exhaustive-deps` | 6 |
| `react-hooks/refs` | 3 |
| `react-refresh/only-export-components` | 3 |

`eslint-plugin-react-hooks` is at `^7.0.1` and the config extends
`reactHooks.configs.flat.recommended`, which turns the React Compiler rules on as errors.
The bulk (`set-state-in-effect`) is a pattern older than those rules, not a regression.

## Why it matters

The `lint` script is the client's only gate. While it exits 1 on known issues, a new one
is indistinguishable from the old ones and the gate is read as noise — the M0 plan itself
had to write "27 problems, all pre-existing" instead of "green".

## What "done" looks like

`npm run lint` exits 0. Each `set-state-in-effect` needs a judgement per component
(derived state computed in render, an event handler, or `useSyncExternalStore`) rather
than a mechanical suppression; `only-export-components` needs file splits. Suppressing a
rule repo-wide is not the goal — the rules are the point.
