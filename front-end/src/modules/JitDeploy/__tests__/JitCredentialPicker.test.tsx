import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import JitCredentialPicker from '../JitCredentialPicker';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const HTTPS = 'https://github.com/o/shop.git';

let list: unknown = [
  { id: 'c1', name: 'GitHub PAT', hostPattern: 'github.com' },
  { id: 'c2', name: 'Work token', hostPattern: null },
];

beforeEach(() => {
  jest.clearAllMocks();
  list = [{ id: 'c1', name: 'GitHub PAT', hostPattern: 'github.com' }, { id: 'c2', name: 'Work token', hostPattern: null }];
  mockApi.get.mockImplementation(async (path: string) => {
    if (path === '/api/jit/p1/credentials') { if (list instanceof Error) throw list; return list as never; }
    throw new Error('unexpected GET ' + path);
  });
});

const setup = (props: Partial<React.ComponentProps<typeof JitCredentialPicker>> = {}) => {
  const onChange = jest.fn();
  render(<JitCredentialPicker base="/api/jit/p1" repoUrl={HTTPS} value={null} onChange={onChange} {...props} />);
  return { onChange };
};

describe('JitCredentialPicker', () => {
  it('lists the saved credentials by name (never a value) for an https address', async () => {
    setup();
    const select = await screen.findByLabelText('Saved credential');
    await screen.findByRole('option', { name: /GitHub PAT/ });
    expect(screen.getByRole('option', { name: /Work token/ })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: /no saved credential/i })).toBeInTheDocument();
    expect(select).toHaveValue('');
  });

  it('chooses one, and can go back to none', async () => {
    const { onChange } = setup();
    const select = await screen.findByLabelText('Saved credential');
    await screen.findByRole('option', { name: /GitHub PAT/ });
    fireEvent.change(select, { target: { value: 'c1' } });
    expect(onChange).toHaveBeenCalledWith('c1');
    fireEvent.change(select, { target: { value: '' } });
    expect(onChange).toHaveBeenLastCalledWith(null);
  });

  it('shows the chosen one', async () => {
    setup({ value: 'c2' });
    await screen.findByRole('option', { name: /Work token/ });
    expect(screen.getByLabelText('Saved credential')).toHaveValue('c2');
  });

  it('says plainly where the token goes, and what it costs, in a tip instead of a paragraph', async () => {
    setup();
    await screen.findByLabelText('Saved credential');
    expect(screen.queryByText(/anyone with administrator access/i)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Help: Saved credential/ }));
    expect(screen.getByRole('tooltip')).toHaveTextContent(/never saved in the address/i);
    expect(screen.getByRole('tooltip')).toHaveTextContent(/administrator access to the server could see it/i);
  });

  it('does not appear for an SSH address, where a deploy key is the way', () => {
    setup({ repoUrl: 'git@github.com:o/shop.git' });
    expect(screen.queryByLabelText('Saved credential')).not.toBeInTheDocument();
    expect(mockApi.get).not.toHaveBeenCalled();
  });

  it('does not appear without an address', () => {
    setup({ repoUrl: '' });
    expect(screen.queryByLabelText('Saved credential')).not.toBeInTheDocument();
  });

  it('points to where credentials are saved when there are none', async () => {
    list = [];
    setup();
    expect(await screen.findByText(/no saved credentials yet/i)).toBeInTheDocument();
    expect(screen.queryByLabelText('Saved credential')).not.toBeInTheDocument();
  });

  it('keeps a credential that was chosen but no longer exists visible, so it can be changed', async () => {
    setup({ value: 'gone' });
    await screen.findByRole('option', { name: /GitHub PAT/ });
    expect(screen.getByText(/the credential you chose is not in the list/i)).toBeInTheDocument();
  });

  it('stays quiet when the list cannot be loaded', async () => {
    list = new Error('nope');
    setup();
    await waitFor(() => expect(mockApi.get).toHaveBeenCalled());
    expect(screen.queryByLabelText('Saved credential')).not.toBeInTheDocument();
  });
});
