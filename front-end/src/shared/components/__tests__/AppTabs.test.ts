import { readFileSync } from 'fs';
import path from 'path';

describe('AppTabs styling', () => {
  it('keeps the active pill behind the tab label', () => {
    const css = readFileSync(path.join(__dirname, '..', 'AppTabs.module.css'), 'utf8');
    const pill = css.match(/\.pill\s*\{([^}]*)\}/)?.[1] ?? '';

    // A positioned child at z-index 0 paints over the label; the library's own pill uses -1.
    expect(pill).toMatch(/z-index:\s*-1/);
  });
});
