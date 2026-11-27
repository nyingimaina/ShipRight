# ShipRight Design Language

**Status:** canonical  
**Applies to:** every new page or component, and every existing surface that is edited or refactored.  
**Companion:** `AGENTS.md` (short memory rules) and `SHIPRIGHT-DEPLOYMENT-BRS.md` (product requirements).

This document is the source of truth for how ShipRight looks, reads and behaves. If code and this document disagree, the document wins unless a written decision says otherwise.

---

## 1. What “Apple-esque” means here

It is not a skin. It is:

- **Clarity** — one job per screen, one primary action, no jargon in the novice view.
- **Deference** — the interface gets out of the way; the person’s project is the hero, not the chrome.
- **Depth** — hierarchy through spacing, type and surfaces, not through borders and boxes.
- **Restraint** — no gratuitous animation, gradients, badges or colours.
- **Predictability** — the same action looks and behaves the same everywhere.
- **Consequence-first** — before anything irreversible: what changes, what it costs, how to undo it.

If a screen feels busy, remove something. If a decision feels scary, explain it. If an action is irreversible, confirm it with a summary, not a scary modal.

---

## 2. Principles

1. **One primary action per screen.** Secondary actions go in a split menu, a sheet or a details section.
2. **Outcome first, mechanism second.** “Put this app live” before “JIT build-on-server”.
3. **Show consequences before acting.** Downtime, cost, exposure, data changes and rollback.
4. **No dead ends.** Every error says what happened, why, and the next step.
5. **Progressive disclosure.** Novice view → Technical View → Legacy.
6. **Calm surfaces.** Generous space, quiet colour, deliberate motion.
7. **Mobile-first for status; desktop-aware for configuration.**
8. **Accessible by default.** Keyboard, screen reader, contrast, reduced motion.
9. **Consistent by construction.** Use shared components and tokens; no one-off styling.
10. **Every state is designed.** Empty, loading, partial, error, success, no-permission, offline.

---

## 3. Tokens

Use CSS variables from `globals.css` and the shared component layer. Do not hardcode colours, spacing, radii or shadows.

### Typography

| Role | Family | Size | Weight | Line height |
|---|---|---|---|---|
| Page title | Manrope | 32 px | 700 | 1.15 |
| Section title | Manrope | 20 px | 650 | 1.25 |
| Body | Manrope | 14–16 px | 400–500 | 1.5 |
| Small / meta | Manrope | 12.5 px | 400–500 | 1.4 |
| Technical | JetBrains Mono | 12–13 px | 400 | 1.5 |

Rules:

- Manrope is the UI typeface. JetBrains Mono is only for code, logs and Technical View.
- Sentence case everywhere. No ALL CAPS except short labels with letter-spacing.
- Numbers in tables use tabular figures where available.
- Never use more than three weights on one screen.

### Colour

Use semantic tokens:

- `--accent` — brand gold; primary actions and focus.
- `--bg-card` — raised surfaces.
- `--border-subtle` — quiet dividers.
- `--text-primary`, `--text-secondary`, `--text-muted`.
- Success `#34c759`, warning `#e0a82e`, danger `#e5534b`.

Rules:

- Never use colour alone to communicate status. Pair with an icon and a word.
- Dark and light must both meet WCAG AA.
- Do not introduce a new brand colour for a single page.
- Do not use gold for errors or red for primary actions.

### Spacing and layout

- 4 / 8 / 12 / 16 / 20 / 24 / 32 / 48.
- Page max-width: 1120 px.
- Card padding: 20–28 px desktop, 16 px mobile.
- Section gap: 24–32 px.
- Form row gap: 6–12 px.
- Touch target: 44 px minimum.

### Shape and elevation

- Radius: 10 px inputs, 12 px cards, 16 px sheets, 980 px pills.
- Shadows: two levels only — resting and raised.
- Borders: 1 px `--border-subtle`; 2 px for outline buttons.
- Avoid nesting cards inside cards more than one level.

### Motion

- 150–250 ms for state changes.
- Spring only for success/confirmation; never for layout.
- Honour `prefers-reduced-motion`.
- No looping animation except a loading indicator.
- Do not animate content on every render.

---

## 4. Components

Use the shared `App*` layer. If a component does not exist, add it there first; do not create a one-off in a page.

| Component | Required behaviour |
|---|---|
| `AppPage` | Page width, padding, title area, mobile spacing |
| `AppHeader` | Fixed 76 px bar, centred against the hamburger, mobile-safe |
| `AppCard` | Quiet surface, 12 px radius, optional header/action |
| `AppSection` | Title, description, content, divider |
| `AppButton` | Zest button wrapper; one primary style per screen |
| `AppSplitButton` | Primary action + secondary menu (Zest split button) |
| `AppTabs` | Shared tab surface; active pill behind the label |
| `AppStatusPill` | Icon + word + colour; never colour alone |
| `AppInlineAlert` | Info/warn/error with title, body, action |
| `AppEmptyState` | What this is, why it matters, one action |
| `AppSheet` | Confirmations and short forms on mobile |
| `AppTechnicalDetails` | Disclosure for raw config/logs; monospace inside |
| `AppSwitchRow` | Label, description, switch, persisted state |
| `AppProviderCard` | Outcome, requirements, resources, risk, badge |
| `AppProgressTimeline` | Phase, state, one sentence per step |
| `AppDiffView` | File path, added/removed lines, approve/reject |
| `AppDiagnosticsLink` | Download link with a plain description |

Rules:

- Buttons: one primary per screen. Secondary = outline or text. Destructive = danger, with confirmation.
- Inputs: visible label, inline validation, error text below, no placeholder-only labels.
- Toggles: label + short description; state is visible and persisted.
- Tables: no horizontal scroll on mobile; use cards or a responsive table.
- Modals: use a sheet on mobile; never trap focus; Esc closes.
- Links: underline on hover; external links show an icon.

---

## 5. Page recipes

### 5.1 Wizard

- One blocking question at a time.
- Progress (“Step 3 of 7”) and a way back.
- Recommended answer preselected; “Use recommended” available.
- “Why this?” disclosure with evidence.
- Never lose a draft.
- Final review before save/apply.

### 5.2 Status / detail

- One headline: what is true now.
- One primary action: the next thing to do.
- Consequence summary: downtime, cost, exposure, rollback.
- Secondary details below.
- Technical View available.

### 5.3 Empty

- Explain the value in one sentence.
- One primary action.
- No technical prerequisites in the novice view.
- A small “What is this?” disclosure.

### 5.4 Error

- What happened, in plain language.
- Why, if known.
- The next step, with one action.
- Diagnostics download.
- Technical View for raw details.
- Never blame the user. Never say only “invalid”.

### 5.5 Loading

- Skeleton that matches the final layout.
- No spinner on an empty page.
- No layout shift when data arrives.
- A slow operation says what it is doing and offers cancel where safe.

### 5.6 Confirmation

- Use a sheet or inline panel, not a browser `confirm`.
- Show the exact target, version and consequence.
- Primary action names the consequence (“Deploy version 4.13.3”), not just “OK”.
- Cancel is always available.

---

## 6. Technical View

- Per-user, persisted in settings.
- A lens, not a permission and not a separate app.
- Reveals raw YAML, command plans, logs, IDs, commit SHA, ports, volumes, env key names.
- Never reveals secrets, tokens, keys or `.env` values.
- Does not remove cost, downtime, exposure or risk from the novice view.
- Uses `AppTechnicalDetails` and JetBrains Mono.
- Keyboard accessible and labelled.

Examples:

```text
Novice:  “The database is private.”
Technical: “db: no published ports; app connects over the shop_default network.”
```

---

## 7. Legacy

- A temporary door to the classic deployment screens.
- Default off for new users; per-user toggle; company admin may hide.
- Direct links still work with a banner.
- Never bypasses rules, health checks or audit.
- Usage is measured.
- Removal date announced at least 90 days ahead; 12 months after GA.

Wording:

> “Classic deployment screen. The new deployment screen is recommended. This view will be removed on <date>.”

---

## 8. Copy

### Voice

- Plain, calm, specific.
- Active voice.
- Sentence case.
- Say what the person gets.

### Rules

- No jargon in the novice view: no “JIT”, “container”, “reverse proxy”, “CVE”, “SBOM”.
- Explain an acronym the first time it is unavoidable.
- Use “you” and “your”.
- Never blame the user.
- Never say “invalid” without saying what to do.
- Use real units and ranges: “about 30 seconds”, “~40 MB”, “2 of 3 services”.
- Name the consequence in the button when it is irreversible.

### Examples

| Instead of | Write |
|---|---|
| “Invalid configuration” | “The compose file is missing a service name. Add a name under `services:`.” |
| “Deployment failed” | “The new version did not become healthy. The previous version is running again.” |
| “JIT build-on-server” | “Build it on the server it runs on.” |
| “Click here” | “Open the deployment plan.” |
| “Error 500” | “ShipRight could not reach the server. Check the connection and try again.” |

---

## 9. Accessibility

- WCAG 2.2 AA.
- Every action reachable by keyboard.
- Visible focus ring on every interactive element.
- Labels for every input, switch and icon-only button.
- Status and errors announced to screen readers.
- No colour-only meaning.
- 44 px touch targets.
- `prefers-reduced-motion` respected.
- Dark and light parity.
- No horizontal scroll on a 360 px viewport.

---

## 10. Responsive behaviour

- Mobile-first for status, alerts and deployment monitoring.
- Cards stack; tables become cards or scroll within their own container.
- The header is 76 px and content clears it.
- Primary actions remain reachable with one hand.
- Technical View may scroll horizontally inside its own container; the page must not.

---

## 11. Review checklist

Before opening a PR that touches UI:

- [ ] One primary action per screen.
- [ ] Consequence summary present for irreversible actions.
- [ ] Empty, loading, error and no-permission states designed.
- [ ] Technical View available and free of secrets.
- [ ] Legacy handled or explicitly out of scope.
- [ ] Shared `App*` components used; no one-off styling.
- [ ] Tokens only; no hardcoded colours/spacing.
- [ ] Mobile and desktop checked.
- [ ] Keyboard and screen-reader paths checked.
- [ ] Copy reviewed against the examples.
- [ ] Visual regression and accessibility tests updated.
- [ ] No new design pattern without a note in this document.

---

## 12. Do / Don’t examples

### Do

- “Your app is live at https://shop.example.com. The previous version is kept for rollback.”
- A split button with one primary action and a short menu.
- A single “What will happen” panel before deploy.
- A warning with a fix and a link to the diff.

### Don’t

- A page with five equally weighted buttons.
- A raw log as the primary deployment experience.
- A “technical details” section that hides cost or downtime.
- A toggle that silently disables a security rule.
- A modal that says only “Are you sure?”.
- A custom button with a new colour because the page felt special.
