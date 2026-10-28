export interface EnvWarning {
  /** 1-based line in the .env text. */
  line: number;
  key: string;
  /** The reserved character found. */
  chars: string;
  message: string;
}

/**
 * Finds values in .env text that Docker Compose, a Linux shell or Windows may read as something other
 * than literal text. Reports where (line and key) and what to do — never the value itself, since
 * these lines hold secrets.
 */
export function envFileWarnings(text: string): EnvWarning[] {
  const warnings: EnvWarning[] = [];
  text.split(/\r?\n/).forEach((raw, i) => {
    const line = raw.trim();
    if (!line || line.startsWith('#')) return;
    const eq = raw.indexOf('=');
    if (eq <= 0) return;

    const key = raw.slice(0, eq).trim().replace(/^export\s+/, '');
    const value = raw.slice(eq + 1).trim();
    const quoted = /^(["'])(.*)\1$/.exec(value);
    const singleQuoted = quoted?.[1] === "'";
    const inner = quoted ? quoted[2] : value;
    const flag = (chars: string, message: string) => warnings.push({ line: i + 1, key, chars, message });

    // Single quotes are taken literally by Compose and by shells, so $ ` \ inside them are safe.
    if (!singleQuoted) {
      if (/\$/.test(inner.replace(/\$\$/g, '')))
        flag('$', `Line ${i + 1} (${key}): contains "$". Docker Compose treats $ as the start of a variable, so part of the value can silently become blank. Write $$ for a literal $.`);
      if (inner.includes('`'))
        flag('`', `Line ${i + 1} (${key}): contains a backtick. A shell reads text between backticks as a command to run. Avoid it, or wrap the whole value in single quotes.`);
      if (inner.includes('\\'))
        flag('\\', `Line ${i + 1} (${key}): contains a backslash. It may be read as an escape (for example \\n becomes a new line) instead of a literal backslash. Use a forward slash, or wrap the value in single quotes.`);
    }
    if (/%\w+%/.test(inner))
      flag('%', `Line ${i + 1} (${key}): contains %NAME%. Windows replaces that with the variable's value. Change it if you want the literal text.`);
    if (!quoted && /\s#/.test(value))
      flag('#', `Line ${i + 1} (${key}): a "#" after a space starts a comment, so everything after it is dropped. Wrap the value in quotes if the # is part of it.`);
    if (!quoted && /["']/.test(value)) {
      const q = value.includes('"') ? '"' : "'";
      flag(q, `Line ${i + 1} (${key}): contains a quote character in the middle of the value. It may be removed or break the line. Wrap the whole value in the other kind of quote.`);
    }
  });
  return warnings;
}
