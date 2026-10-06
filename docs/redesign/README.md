# SmartSolar redesign — design handoff

This folder is the design source of truth for rebuilding the web and mobile UI.

| File | What it is |
|---|---|
| `CLAUDE_CODE_PROMPT.md` | The task prompt for Claude Code: phases, rules, quality gates, definition of done. |
| `DESIGN_SPEC.md` | The implementation spec: tokens, components, navigation and routes, screen-to-code mapping, behaviours B1–B14, gap matrix. |
| `design/index.html` | A gallery of every mockup. Open it in a browser. |
| `design/*.dc.html` | Static HTML mockups at real size: mobile 390 px wide, web 1440 px wide and fluid. `Before-*` show the old app (audit only). |
| `design/canvas.json` | The design canvas index (board titles, sizes, grouping). |
| `tokens.json` | Design tokens (light and dark): colors, type scale, spacing, radii, shadows, chart rules. |
| `assets/smartsolar-mark.svg`, `assets/smartsolar-app-icon.svg` | The brand mark and a full-bleed app-icon source. |

How to start: run Claude Code from the repository root and paste the contents of `CLAUDE_CODE_PROMPT.md`, or tell it to read and follow that file. It produces `IMPLEMENTATION_PLAN.md` here first and waits for approval.
