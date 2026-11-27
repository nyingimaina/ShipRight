# ShipRight — Repository Memory

This file is read automatically when working in this repository. It is short on purpose. The canonical documents are:

- `SHIPRIGHT-DEPLOYMENT-BRS.md` — product requirements, decisions and acceptance criteria.
- `docs/design/design-language.md` — visual and interaction rules.

## Design language (must apply)

When creating a new page or component, or editing/refactoring an existing one, apply `docs/design/design-language.md`.

Non-negotiables:

- One primary action per screen. Secondary actions go in a split menu or a details section.
- Outcome first, mechanism second. No jargon in the novice view.
- Show consequences before irreversible actions: downtime, cost, exposure, rollback.
- Every error says what happened, why, and the next step. No dead ends.
- Use shared `App*` components and CSS tokens. Do not create one-off buttons, cards, tabs, colours or spacing.
- Design every state: empty, loading, partial, error, no-permission, offline, success.
- Mobile-first for status and monitoring; desktop-aware for configuration.
- WCAG 2.2 AA: keyboard, focus, labels, contrast, reduced motion, dark parity.
- **Technical View** is a per-user lens. It reveals raw config, plans, logs and IDs, never secrets.
- **Legacy** is a temporary door to classic screens. It never bypasses rules, health checks or audit.
- Technical names (JIT, compose, CVE, SBOM) belong in Technical View, not the novice view.

If a design decision is missing, add it to `docs/design/design-language.md` before inventing a new pattern.

## Product rules (must apply)

- The deterministic wizard must work with AI switched off.
- AI proposes; ShipRight judges and proves; a human approves irreversible actions.
- The AI layer is **Hakiri**, at `D:\work\nyingi\code\systems\hakiri`. Do not add ShipRight/DevTeam/Tinkerdeck types or prompts to it.
- Rules are typed and checkable. A narrower scope may tighten but not weaken a locked rule. Weakening requires a time-bound waiver.
- The security floor is locked. Convenience is an environment-scoped exception, never a global slider.
- Every deployment records the exact commit SHA and a rollback path.
- A dry run must be safe against production and must change nothing.
- The security monitor is SBOM-first, source-accurate (distro/vendor), and prioritises KEV/EPSS/exposure over raw CVSS.
- No automatic production deploy of security fixes by default.

## Engineering rules

- TDD: write the failing test first, then production code, then run the full suite.
- Keep changes consistent with the surrounding code and patterns.
- Prefer additive changes; do not rewrite working methods unless the change requires it.
- Update the BRS/design doc when a decision changes.
