import { describeEnv, envKeys, envSummary } from '../envVars';

describe('envKeys', () => {
  it('lists the names in order, skipping comments and blank lines', () => {
    expect(envKeys('# note\n\nA=1\nexport B_2=x\nC=\n')).toEqual(['A', 'B_2', 'C']);
  });

  it('does not mistake the inside of a multi-line quoted value for variables', () => {
    expect(envKeys('CERT="-----BEGIN\nLOG=inside\n-----END"\nKEEP=1')).toEqual(['CERT', 'KEEP']);
  });

  it('copes with nothing at all', () => {
    expect(envKeys('')).toEqual([]);
    expect(envKeys(null)).toEqual([]);
    expect(envKeys(undefined)).toEqual([]);
  });

  it('lists a name once', () => {
    expect(envKeys('A=1\nA=2')).toEqual(['A']);
  });
});

describe('envSummary', () => {
  it('counts each side and names what the project replaces', () => {
    expect(envSummary('DB=a\nLOG=info\nKEY=s', 'LOG=debug\nEXTRA=1')).toEqual({ fromServer: 2, fromProject: 2, overridden: ['LOG'] });
  });
});

describe('describeEnv', () => {
  it('says it in plain words and never shows a value', () => {
    const text = describeEnv(envSummary('KEY=topsecret\nLOG=info', 'LOG=hunter2'));
    expect(text).toMatch(/1 from the server settings/);
    expect(text).toMatch(/1 from this project/);
    expect(text).toMatch(/LOG/);
    expect(text).not.toMatch(/topsecret|hunter2|info/);
  });

  it('says nothing is set when both sides are empty', () => {
    expect(describeEnv(envSummary('', ''))).toMatch(/no variables/i);
  });
});
