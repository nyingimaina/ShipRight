import { applyPathChanges } from '../pathRefresh';
import { emptyServiceDraft, ServiceDraft } from '../atoms/types';
import { IPathChange } from '@/shared/types/IPathRefresh';

function svc(overrides: Partial<ServiceDraft>): ServiceDraft {
  return { ...emptyServiceDraft(), ...overrides };
}

const change = (serviceName: string, newContext: string, newVersion = 'v.txt'): IPathChange => ({
  serviceName,
  oldVersionFilePath: 'v.txt',
  newVersionFilePath: newVersion,
  oldBuildContextPath: 'old',
  newBuildContextPath: newContext,
});

describe('applyPathChanges', () => {
  it('updates the build context and version file of the named service', () => {
    const result = applyPathChanges(
      [svc({ name: 'api', buildContextPath: 'old', versionFilePath: 'v.txt' })],
      [change('api', 'new', 'new/v.txt')],
    );

    expect(result[0].buildContextPath).toBe('new');
    expect(result[0].versionFilePath).toBe('new/v.txt');
  });

  it('leaves every other field of the service exactly as it was, passwords included', () => {
    const before = svc({
      name: 'api',
      buildContextPath: 'old',
      versionFilePath: 'v.txt',
      dockerImageName: 'nyingi/api',
      dockerUsername: 'user',
      dockerPassword: 'secret',
      dockerRegistry: 'ghcr.io',
      dockerRegistryResourceId: 'reg-1',
      composeServiceName: 'api',
      imageRetentionCount: 7,
      localImageKeepCount: 3,
      version: '1.2.3',
    });

    const [after] = applyPathChanges([before], [change('api', 'new')]);

    expect({ ...after, buildContextPath: 'old' }).toEqual(before);
  });

  it('never changes a name, and does not touch services that have no change', () => {
    const services = [svc({ name: 'api', buildContextPath: 'a' }), svc({ name: 'web', buildContextPath: 'w' })];

    const result = applyPathChanges(services, [change('api', 'new')]);

    expect(result.map(s => s.name)).toEqual(['api', 'web']);
    expect(result[1]).toBe(services[1]);
  });

  it('ignores a change for a service that is not in the list and does not mutate its input', () => {
    const services = [svc({ name: 'api', buildContextPath: 'a' })];
    const snapshot = JSON.stringify(services);

    const result = applyPathChanges(services, [change('ghost', 'new')]);

    expect(result).toEqual(services);
    expect(JSON.stringify(services)).toBe(snapshot);
  });
});
