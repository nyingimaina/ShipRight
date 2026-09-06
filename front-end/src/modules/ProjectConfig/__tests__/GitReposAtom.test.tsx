import { render, screen, fireEvent } from '@testing-library/react';
import GitReposAtom from '../atoms/GitReposAtom';
import { GitRepoDraft, emptyGitRepoDraft } from '../atoms/types';

jest.mock('@/shared/ApiService', () => ({
  api: { post: jest.fn() },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const github = { id: 'cred-github', name: 'github pat', value: 'ghp_x', tags: ['git', 'github'], createdAt: '', modifiedAt: '' } as any;
const devops = { id: 'cred-devops', name: 'azure devops', value: 'pat', tags: ['azure'], createdAt: '', modifiedAt: '' } as any;

function repo(overrides: Partial<GitRepoDraft> = {}): GitRepoDraft {
  return { ...emptyGitRepoDraft(), repoPath: '/repo', ...overrides };
}

describe('GitReposAtom credential picker', () => {
  it('renders all credentials', () => {
    render(
      <GitReposAtom
        repos={[repo()]}
        credentials={[github, devops]}
        errors={{}}
        onReposChange={jest.fn()}
      />
    );
    expect(screen.getByRole('option', { name: 'github pat' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'azure devops' })).toBeInTheDocument();
  });

  it('filters credentials by selected tag (exclusive)', () => {
    render(
      <GitReposAtom
        repos={[repo()]}
        credentials={[github, devops]}
        errors={{}}
        onReposChange={jest.fn()}
      />
    );
    const combos = screen.getAllByRole('combobox');
    const filter = combos[combos.length - 1];
    fireEvent.change(filter, { target: { value: 'git' } });

    const credentialSelect = combos[0];
    const options = Array.from(credentialSelect.querySelectorAll('option')).map(o => o.textContent);
    expect(options).toContain('github pat');
    expect(options).not.toContain('azure devops');
  });
});