const PREFIX = '[step] ✓ ';

/** The words of a "[step] ✓ what (seconds)" line the server sends when a step finishes, or null for any other line. */
export function stepText(line: string): string | null {
  if (!line.startsWith(PREFIX)) return null;
  const text = line.slice(PREFIX.length).trim();
  return text.length > 0 ? text : null;
}
