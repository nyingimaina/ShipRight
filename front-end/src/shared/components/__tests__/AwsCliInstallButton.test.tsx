import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import AwsCliInstallButton from '../AwsCliInstallButton';

jest.mock('@/shared/ApiService', () => ({
  api: {
    post: jest.fn(),
  },
}));

import { api } from '@/shared/ApiService';

const mockPost = api.post as jest.Mock;

describe('AwsCliInstallButton', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('installs via the auto strategy and reports success', async () => {
    mockPost.mockResolvedValue({ success: true, message: 'Installed AWS CLI v2. Found the AWS CLI at /usr/local/bin/aws.' });
    const onInstalled = jest.fn();
    render(<AwsCliInstallButton onInstalled={onInstalled} />);

    fireEvent.click(screen.getByText('Install AWS CLI'));

    await waitFor(() => {
      expect(mockPost).toHaveBeenCalledWith('/api/resources/aws-profiles/install-cli', { strategy: 'auto' });
    });
    await screen.findByText(/Found the AWS CLI/);
    await waitFor(() => expect(onInstalled).toHaveBeenCalledTimes(1));
  });

  it('shows the manual command and offers the no-sudo pip path', async () => {
    mockPost
      .mockResolvedValueOnce({
        success: false,
        message: 'Installing the AWS CLI needs sudo, which is not available automatically.',
        requiresManualInstall: true,
        command: 'curl -fsSL -o /tmp/awscliv2.zip https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip && unzip -q /tmp/awscliv2.zip -d /tmp && sudo /tmp/aws/install',
      })
      .mockResolvedValueOnce({ success: true, message: 'Installed via pip (user-level, no sudo needed).' });
    render(<AwsCliInstallButton />);

    fireEvent.click(screen.getByText('Install AWS CLI'));

    await screen.findByText(/needs sudo/);
    expect(screen.getByText(/awscliv2\.zip/)).toBeTruthy();

    fireEvent.click(screen.getByText('Try without sudo (pip)'));

    await waitFor(() => {
      expect(mockPost).toHaveBeenLastCalledWith('/api/resources/aws-profiles/install-cli', { strategy: 'pip' });
    });
    await screen.findByText(/no sudo needed/);
  });

  it('shows the output tail on a plain failure', async () => {
    mockPost.mockResolvedValue({ success: false, message: 'Downloading the AWS CLI failed. curl: error', outputTail: 'curl: error' });
    render(<AwsCliInstallButton />);

    fireEvent.click(screen.getByText('Install AWS CLI'));

    await screen.findByText(/Downloading the AWS CLI failed/);
    expect(screen.getAllByText(/curl: error/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getByRole('alert')).toBeTruthy();
  });
});