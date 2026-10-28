import { render, screen } from '@testing-library/react';
import React from 'react';
import ServerDeploySection from '../../modules/ServerDeploy/ServerDeploySection';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => <button onClick={onClick} disabled={disabled}>{children}</button>,
}));
jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => <input value={value} onChange={onChange} placeholder={placeholder} />,
}));

const base = { composeRepoUrl: '', ecrRegistry: '', ecrRegion: '', healthCheckUrl: '' };

describe('ServerDeploySection .env warnings', () => {
  it('warns about a reserved character, naming the line and key but not the value', () => {
    render(<ServerDeploySection serverId="s1" values={{ ...base, envFile: 'A=1\nDB_PASS=secret$mK2' }} onChange={jest.fn()} />);
    const alert = screen.getByRole('status');
    expect(alert.textContent).toMatch(/line 2/i);
    expect(alert.textContent).toMatch(/DB_PASS/);
    expect(alert.textContent).not.toMatch(/secret/);
  });

  it('shows nothing when every value is safe', () => {
    render(<ServerDeploySection serverId="s1" values={{ ...base, envFile: 'A=1\nB=two' }} onChange={jest.fn()} />);
    expect(screen.queryByRole('status')).toBeNull();
  });
});
