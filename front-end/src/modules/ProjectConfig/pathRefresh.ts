import { IPathChange } from '@/shared/types/IPathRefresh';
import { ServiceDraft } from './atoms/types';

// Copies only the two refreshed paths onto the matching services. Names and every other field,
// including credentials, stay exactly as they are.
export function applyPathChanges(services: ServiceDraft[], changes: IPathChange[]): ServiceDraft[] {
  const byName = new Map(changes.map(c => [c.serviceName, c]));
  return services.map(s => {
    const change = byName.get(s.name);
    return change
      ? { ...s, versionFilePath: change.newVersionFilePath, buildContextPath: change.newBuildContextPath }
      : s;
  });
}
