import type { ZestTabItem } from 'jattac.libs.web.zest-tabs';
import type { IProject } from '@/shared/types/IProject';

/**
 * Adds the "Deploy" tab (streams the project's server deploy) right after "Build & Deploy".
 * A project with no server has nothing to deploy to, so its tabs come back unchanged.
 */
export function withDeployTab<T extends string>(
  tabs: ZestTabItem<T>[],
  project: Pick<IProject, 'serverId'>,
): ZestTabItem<T | 'deploy'>[] {
  if (!project.serverId) return tabs;
  const after = tabs.findIndex(t => t.value === 'build');
  const at = after >= 0 ? after + 1 : Math.min(1, tabs.length);
  return [...tabs.slice(0, at), { label: 'Deploy', value: 'deploy' as const }, ...tabs.slice(at)];
}
