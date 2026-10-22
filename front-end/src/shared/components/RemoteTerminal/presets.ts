export interface TerminalPreset {
  label: string;
  /** Plain-language hover text for non-technical users. */
  hint: string;
  /** `{dir}` is replaced by the compose folder; presets that use it are hidden when no folder is known. */
  command: string;
}

export const TERMINAL_PRESETS: TerminalPreset[] = [
  {
    label: 'Why is the database down?',
    hint: 'Shows the database’s last messages, whether it crashed or ran out of memory, and how much memory is free.',
    command: "docker logs --tail 40 shipright-db; docker inspect shipright-db -f '{{.State.ExitCode}} oom={{.State.OOMKilled}}'; free -m",
  },
  { label: 'Database logs', hint: 'The last 40 lines the database printed.', command: 'docker logs --tail 40 shipright-db' },
  {
    label: 'Database crash reason',
    hint: 'Exit code of the database container (0 = clean) and whether the server killed it for using too much memory.',
    command: "docker inspect shipright-db -f '{{.State.ExitCode}} oom={{.State.OOMKilled}}'",
  },
  { label: 'App logs', hint: 'The last 40 lines the ShipRight app printed.', command: 'docker logs --tail 40 shipright-app' },
  {
    label: 'Running containers',
    hint: 'Every container with the image version it runs and whether it is healthy.',
    command: "docker ps -a --format 'table {{.Names}}\\t{{.Image}}\\t{{.Status}}'",
  },
  { label: 'Memory and disk', hint: 'Free memory and free disk space on this server.', command: 'free -m; df -h /' },
  {
    label: 'Images to deploy',
    hint: 'The image versions the compose file would pull right now.',
    command: "cd '{dir}' && docker compose config --images",
  },
  {
    label: 'Deploy history',
    hint: 'The last entries of the log ShipRight keeps on the server for Prepare and Deploy.',
    command: 'tail -n 30 ~/.shipright/checkpoints.log',
  },
];

/** Presets usable right now: those needing the compose folder are dropped when it is unknown. */
export function presetsFor(dir?: string): (TerminalPreset & { resolved: string })[] {
  const d = dir?.trim().replace(/\/+$/, '');
  return TERMINAL_PRESETS
    .filter(p => !p.command.includes('{dir}') || !!d)
    .map(p => ({ ...p, resolved: p.command.replace('{dir}', d ?? '') }));
}
