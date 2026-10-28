import { envFileWarnings } from '../envFileWarnings';

const chars = (text: string) => envFileWarnings(text).map(w => w.chars);

describe('envFileWarnings', () => {
  it('has nothing to say about plain values, blank lines and comments', () => {
    const text = '# settings\n\nAPI_URL=https://example.com/path?x=1&y=2\nPORT=5200\nNAME=my-app_1.2\nEMPTY=\n';
    expect(envFileWarnings(text)).toEqual([]);
  });

  it('flags $ because docker compose reads it as a variable, and says how to write a literal one', () => {
    const [w] = envFileWarnings('DB_PASS=ab$mK2xyz');
    expect(w.chars).toBe('$');
    expect(w.line).toBe(1);
    expect(w.key).toBe('DB_PASS');
    expect(w.message).toMatch(/\$\$/);
  });

  it('accepts a $ that is already doubled, but still flags a lone one beside it', () => {
    expect(chars('A=ab$$cd')).toEqual([]);
    expect(chars('A=ab$$cd$ef')).toEqual(['$']);
    expect(chars('A=${HOME}/x')).toEqual(['$']);
  });

  it('does not flag $ inside single quotes, which are taken literally', () => {
    expect(chars("A='ab$cd'")).toEqual([]);
  });

  it('flags a backtick, a backslash and a Windows-style %VAR%', () => {
    expect(chars('A=ab`whoami`')).toEqual(['`']);
    expect(chars('A=C:\\temp\\x')).toEqual(['\\']);
    expect(chars('A=%USERPROFILE%\\x')).toEqual(expect.arrayContaining(['%']));
  });

  it('flags a # that starts a comment (after a space) but not one inside a value', () => {
    expect(chars('A=abc #not part of the value')).toEqual(['#']);
    expect(chars('A=color#fff')).toEqual([]);
    expect(chars('A="abc #kept"')).toEqual([]);
  });

  it('flags quote characters in the middle of an unquoted value, not a properly quoted whole value', () => {
    expect(chars('A=it"s')).toEqual(['"']);
    expect(chars('A="whole value"')).toEqual([]);
    expect(chars("A='whole value'")).toEqual([]);
  });

  it('reports the line number and key, ignores `export ` prefixes, and can flag several lines', () => {
    const text = 'OK=1\nexport PASS=a$b\n\nOTHER=x`y';
    const ws = envFileWarnings(text);
    expect(ws.map(w => [w.line, w.key])).toEqual([[2, 'PASS'], [4, 'OTHER']]);
  });

  it('never repeats the value in its message', () => {
    const [w] = envFileWarnings('SECRET=topsecret$value');
    expect(JSON.stringify(w)).not.toContain('topsecret');
  });
});
