import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import BuildSourceCard from '../BuildSourceCard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const LABEL = 'Build from a clean copy of my saved code';

describe('BuildSourceCard', () => {
  beforeEach(() => jest.resetAllMocks());

  it.each([undefined, null, 'InPlace'] as const)('is off for projects that build in place (%s)', value => {
    render(<BuildSourceCard projectId="p1" buildSource={value} />);
    expect(screen.getByLabelText(LABEL)).not.toBeChecked();
  });

  it('is on for projects that already use clean copies', () => {
    render(<BuildSourceCard projectId="p1" buildSource="CleanSnapshot" />);
    expect(screen.getByLabelText(LABEL)).toBeChecked();
  });

  it('turns clean copies on and tells the person', async () => {
    mockApi.post.mockResolvedValueOnce({ buildSource: 'CleanSnapshot' });
    render(<BuildSourceCard projectId="p1" buildSource={null} />);

    fireEvent.click(screen.getByLabelText(LABEL));

    await waitFor(() => expect(screen.getByLabelText(LABEL)).toBeChecked());
    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/build-source', { buildSource: 'CleanSnapshot' });
    expect(toast.success).toHaveBeenCalled();
  });

  it('turns it back off by choosing InPlace explicitly', async () => {
    mockApi.post.mockResolvedValueOnce({ buildSource: 'InPlace' });
    render(<BuildSourceCard projectId="p1" buildSource="CleanSnapshot" />);

    fireEvent.click(screen.getByLabelText(LABEL));

    await waitFor(() => expect(screen.getByLabelText(LABEL)).not.toBeChecked());
    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/build-source', { buildSource: 'InPlace' });
  });

  it('puts the switch back and says why when saving fails', async () => {
    mockApi.post.mockRejectedValueOnce({ message: 'Project not found.' });
    render(<BuildSourceCard projectId="p1" buildSource={null} />);

    fireEvent.click(screen.getByLabelText(LABEL));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Project not found.'));
    expect(screen.getByLabelText(LABEL)).not.toBeChecked();
  });

  it('cannot be flipped again while a change is being saved', async () => {
    let finish: (v: unknown) => void = () => {};
    mockApi.post.mockReturnValueOnce(new Promise(res => { finish = res; }));
    render(<BuildSourceCard projectId="p1" buildSource={null} />);

    fireEvent.click(screen.getByLabelText(LABEL));
    expect(screen.getByLabelText(LABEL)).toBeDisabled();

    finish({ buildSource: 'CleanSnapshot' });
    await waitFor(() => expect(screen.getByLabelText(LABEL)).not.toBeDisabled());
  });

  it('explains in plain language what the setting does', () => {
    render(<BuildSourceCard projectId="p1" buildSource={null} />);
    expect(screen.getByText(/exactly what you last saved in git/i)).toBeInTheDocument();
  });
});
