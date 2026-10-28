import { withDeployTab } from '../deployTab';

const tabs = (...values: string[]) => values.map(v => ({ label: v, value: v }));
const project = (serverId?: string) => ({ serverId } as any);

describe('withDeployTab', () => {
  it('adds a Deploy tab straight after Build & Deploy when the project has a server', () => {
    const result = withDeployTab(tabs('overview', 'build', 'logs'), project('srv-1'));
    expect(result.map(t => t.value)).toEqual(['overview', 'build', 'deploy', 'logs']);
    expect(result[2].label).toBe('Deploy');
  });

  it('puts Deploy after Overview when there is no Build & Deploy tab', () => {
    const result = withDeployTab(tabs('overview', 'logs'), project('srv-1'));
    expect(result.map(t => t.value)).toEqual(['overview', 'deploy', 'logs']);
  });

  it('leaves the tabs alone when the project has no server', () => {
    const input = tabs('overview', 'build');
    expect(withDeployTab(input, project(undefined))).toEqual(input);
    expect(withDeployTab(input, project(''))).toEqual(input);
  });

  it('does not change the list it was given', () => {
    const input = tabs('overview', 'build');
    withDeployTab(input, project('srv-1'));
    expect(input).toHaveLength(2);
  });
});
