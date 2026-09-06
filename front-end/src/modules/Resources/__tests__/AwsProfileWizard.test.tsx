import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import AwsProfileWizard, { type AwsProfileWizardInput } from '../AwsProfileWizard';
import type { IAwsCredentialsSource } from '@/shared/types/IProject';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn(),
    post: jest.fn(),
  },
}));

jest.mock('@/shared/serverInfo', () => ({
  useServerMode: () => 'desktop',
}));

import { api } from '@/shared/ApiService';

const mockGet = api.get as jest.Mock;
const mockPost = api.post as jest.Mock;

const sourceWith = (overrides: Partial<IAwsCredentialsSource>): IAwsCredentialsSource => ({
  kind: 'Wsl',
  fileExists: true,
  profiles: [{ name: 'shipright-prod', region: 'eu-west-1', hasKeys: true, unsupportedReason: null }],
  ...overrides,
});

const fullInput = (over: Partial<AwsProfileWizardInput> = {}): AwsProfileWizardInput => ({
  name: '',
  profileName: '',
  accessKeyId: '',
  secretAccessKey: '',
  sessionToken: '',
  defaultRegion: '',
  tags: [],
  ...over,
});

const renderWizard = (over: Partial<AwsProfileWizardInput> = {}) =>
  render(
    <AwsProfileWizard
      initial={fullInput(over)}
      isEdit={false}
      allTags={[]}
      onSave={jest.fn().mockResolvedValue(undefined)}
      onCancel={jest.fn()}
    />,
  );

const goToStep = async (target: number) => {
  let current = 1;
  while (current < target) {
    if (current === 1) {
      fireEvent.click(screen.getByText('Skip — paste keys'));
    } else {
      fireEvent.click(screen.getByText('Next'));
    }
    current += 1;
  }
};

describe('AwsProfileWizard', () => {
  beforeEach(() => {
    mockGet.mockReset();
    mockPost.mockReset();
  });

  it('renders a 4-step stepper', () => {
    renderWizard();
    expect(screen.getByLabelText('Step 1: Credentials')).toBeInTheDocument();
    expect(screen.getByLabelText('Step 2: Who is this?')).toBeInTheDocument();
    expect(screen.getByLabelText('Step 3: Region & test')).toBeInTheDocument();
    expect(screen.getByLabelText('Step 4: Save')).toBeInTheDocument();
  });

  it('imports a profile found on the machine and pre-fills region', async () => {
    mockGet.mockResolvedValue(sourceWith({}));
    renderWizard();

    fireEvent.click(screen.getByText('Find profiles on this machine'));

    expect(await screen.findByText(/Found 1 profile/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Import a profile'), { target: { value: 'shipright-prod' } });

    await waitFor(() => {
      expect(screen.getByText('Test connection')).toBeInTheDocument();
    });
  });

  it('shows guidance when no credentials file exists', async () => {
    mockGet.mockResolvedValue(sourceWith({ fileExists: false, profiles: [] }));
    renderWizard();

    fireEvent.click(screen.getByText('Find profiles on this machine'));

    expect(await screen.findByText(/No AWS credentials file found/)).toBeInTheDocument();
  });

  it('flags unsupported SSO/credential_process profiles', async () => {
    mockGet.mockResolvedValue(sourceWith({
      profiles: [
        { name: 'sso-user', region: null, hasKeys: false, unsupportedReason: 'Uses AWS SSO — not supported.' },
      ],
    }));
    renderWizard();

    fireEvent.click(screen.getByText('Find profiles on this machine'));

    expect(await screen.findByText(/SSO \/ credential_process/)).toBeInTheDocument();
  });

  it('skips import and shows the paste-keys step', () => {
    renderWizard();
    fireEvent.click(screen.getByText('Skip — paste keys'));

    expect(screen.getByLabelText('Credential source')).toBeInTheDocument();
  });

  it('validates explicit keys and shows the account id', async () => {
    mockPost.mockResolvedValue({
      ok: true,
      accountId: '123456789012',
      arn: 'arn:aws:iam::123456789012:user/builder',
    });
    renderWizard({ name: 'Prod', accessKeyId: 'AKIA123', secretAccessKey: 'secret' });
    await goToStep(3);

    fireEvent.click(screen.getByText('Test connection'));

    expect(await screen.findByText(/Connected/)).toBeInTheDocument();
    expect(mockPost).toHaveBeenCalledWith('/api/resources/aws-profiles/validate', {
      profileId: null,
      profile: expect.objectContaining({ accessKeyId: 'AKIA123', secretAccessKey: 'secret' }),
    });
  });

  it('validates a named profile and shows guided fix for missing profile', async () => {
    mockPost.mockResolvedValue({
      ok: false,
      errorCode: 'profile-not-found',
      message: 'not found',
      hint: 'Run "aws configure --profile shipright-prod" on this machine.',
    });
    renderWizard({ name: 'Prod', profileName: 'shipright-prod' });
    await goToStep(3);

    fireEvent.click(screen.getByText('Test connection'));

    expect(await screen.findByText(/aws configure/)).toBeInTheDocument();
    expect(mockPost).toHaveBeenCalledWith('/api/resources/aws-profiles/validate', {
      profileId: null,
      profile: expect.objectContaining({ profileName: 'shipright-prod' }),
    });
  });

  it('offers to install the AWS CLI when it is missing, then re-tests', async () => {
    mockPost.mockResolvedValueOnce({
      ok: false,
      errorCode: 'aws-cli-missing',
      message: 'The AWS CLI could not be run on the build machine.',
      hint: 'Install AWS CLI on the build machine.',
    });
    mockPost.mockResolvedValueOnce({ success: true, message: 'Installed AWS CLI v2. Found the AWS CLI at /usr/local/bin/aws.' });
    mockPost.mockResolvedValue({ ok: true, accountId: '123456789012' });
    renderWizard({ name: 'Prod', accessKeyId: 'AKIA123', secretAccessKey: 'secret' });
    await goToStep(3);

    fireEvent.click(screen.getByText('Test connection'));

    expect(await screen.findByText('Install AWS CLI')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Install AWS CLI'));

    await waitFor(() => {
      expect(mockPost).toHaveBeenCalledWith('/api/resources/aws-profiles/install-cli', { strategy: 'auto' });
    });
    expect(await screen.findByText(/Connected/)).toBeInTheDocument();
    expect(mockPost).toHaveBeenCalledWith('/api/resources/aws-profiles/validate', expect.anything());
    expect(mockPost).toHaveBeenCalledTimes(3);
  });

  it('saves a named profile without key fields', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(
      <AwsProfileWizard
        initial={{ name: 'Prod', profileName: 'shipright-prod', defaultRegion: 'eu-west-1' }}
        isEdit={false}
        allTags={[]}
        onSave={onSave}
        onCancel={jest.fn()}
      />,
    );
    await goToStep(4);

    fireEvent.click(screen.getByText('Create AWS Profile'));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
        name: 'Prod',
        profileName: 'shipright-prod',
        accessKeyId: undefined,
      }));
    });
  });

  it('saves explicit keys without a profile name', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(
      <AwsProfileWizard
        initial={{ name: 'Keys', accessKeyId: 'AKIA12', secretAccessKey: 's3cret' }}
        isEdit={false}
        allTags={[]}
        onSave={onSave}
        onCancel={jest.fn()}
      />,
    );
    await goToStep(4);

    fireEvent.click(screen.getByText('Create AWS Profile'));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
        accessKeyId: 'AKIA12',
        secretAccessKey: 's3cret',
        profileName: undefined,
      }));
    });
  });

  it('requires a name before saving', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(
      <AwsProfileWizard
        initial={{ name: '' }}
        isEdit={false}
        allTags={[]}
        onSave={onSave}
        onCancel={jest.fn()}
      />,
    );
    await goToStep(4);

    fireEvent.click(screen.getByText('Create AWS Profile'));

    await waitFor(() => {
      expect(onSave).not.toHaveBeenCalled();
    });
  });
});