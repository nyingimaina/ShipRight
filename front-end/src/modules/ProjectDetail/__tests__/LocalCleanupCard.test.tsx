import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import LocalCleanupCard from '../LocalCleanupCard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const services = [
  { name: 'Api', localImageKeepCount: 0 },
  { name: 'Web', skipLocalCleanup: true },
  { name: 'Worker' },
];

describe('LocalCleanupCard', () => {
  beforeEach(() => jest.resetAllMocks());

  it('renders nothing for a project without services', () => {
    const { container } = render(<LocalCleanupCard projectId="p1" services={[]} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('shows one switch per service, on only where the service opted out', () => {
    render(<LocalCleanupCard projectId="p1" services={services} />);

    expect(screen.getByLabelText('Keep Api images on this computer')).not.toBeChecked();
    expect(screen.getByLabelText('Keep Web images on this computer')).toBeChecked();
    expect(screen.getByLabelText('Keep Worker images on this computer')).not.toBeChecked();
  });

  it('says what will happen to each service that is not opted out', () => {
    render(<LocalCleanupCard projectId="p1" services={services} />);

    expect(screen.getByText(/deleted from this computer right after they are pushed/i)).toBeInTheDocument();
    expect(screen.getByText(/newest 2 versions are kept/i)).toBeInTheDocument();
  });

  it('opts a service out and tells the person', async () => {
    mockApi.post.mockResolvedValueOnce({} as never);
    render(<LocalCleanupCard projectId="p1" services={services} />);

    fireEvent.click(screen.getByLabelText('Keep Api images on this computer'));

    await waitFor(() => expect(screen.getByLabelText('Keep Api images on this computer')).toBeChecked());
    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/local-cleanup', { service: 'Api', skipLocalCleanup: true });
    expect(toast.success).toHaveBeenCalled();
  });

  it('opts a service back in', async () => {
    mockApi.post.mockResolvedValueOnce({} as never);
    render(<LocalCleanupCard projectId="p1" services={services} />);

    fireEvent.click(screen.getByLabelText('Keep Web images on this computer'));

    await waitFor(() => expect(screen.getByLabelText('Keep Web images on this computer')).not.toBeChecked());
    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/local-cleanup', { service: 'Web', skipLocalCleanup: false });
  });

  it('leaves the switch as it was when saving fails', async () => {
    mockApi.post.mockRejectedValueOnce({ message: 'nope' });
    render(<LocalCleanupCard projectId="p1" services={services} />);

    fireEvent.click(screen.getByLabelText('Keep Api images on this computer'));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('nope'));
    expect(screen.getByLabelText('Keep Api images on this computer')).not.toBeChecked();
  });
});
