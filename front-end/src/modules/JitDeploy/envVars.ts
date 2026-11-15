/**
 * Reads the names out of .env-style text and compares the server's variables with the project's. It mirrors what the server
 * does when it merges them (the project wins, per name). Only names are ever produced, because these lines hold secrets.
 */

export interface EnvSummary {
  fromServer: number;
  fromProject: number;
  overridden: string[];
}

const ASSIGNMENT = /^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.-]*)\s*(=(.*))?$/;

/** True when the text holds the closing quote (a double quote escaped with a backslash does not count). */
function closes(text: string, quote: string): boolean {
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '\\' && quote === '"') { i++; continue; }
    if (text[i] === quote) return true;
  }
  return false;
}

/** The variable names, in order, each once. A quoted value that runs over several lines is one variable. */
export function envKeys(text: string | null | undefined): string[] {
  const keys: string[] = [];
  const lines = (text ?? '').replace(/\r\n?/g, '\n').split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim() || line.trimStart().startsWith('#')) continue;
    const m = ASSIGNMENT.exec(line);
    if (!m) continue;
    if (!keys.includes(m[1])) keys.push(m[1]);
    const value = (m[3] ?? '').trimStart();
    const quote = value[0];
    if ((quote === '"' || quote === "'") && !closes(value.slice(1), quote)) {
      while (i + 1 < lines.length) {
        i++;
        if (closes(lines[i], quote)) break;
      }
    }
  }
  return keys;
}

export function envSummary(server: string | null | undefined, project: string | null | undefined): EnvSummary {
  const s = envKeys(server);
  const p = new Set(envKeys(project));
  const overridden = s.filter(k => p.has(k));
  return { fromServer: s.length - overridden.length, fromProject: p.size, overridden };
}

export function describeEnv(s: EnvSummary): string {
  if (s.fromServer + s.fromProject + s.overridden.length === 0) return 'No variables are set, so none are passed to compose.';
  const base = `${s.fromServer} from the server settings, ${s.fromProject} from this project`;
  return s.overridden.length > 0 ? `${base} (overriding: ${s.overridden.join(', ')})` : base;
}
