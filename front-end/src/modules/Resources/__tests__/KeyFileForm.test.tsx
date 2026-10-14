import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import KeyFileForm from '../KeyFileForm';

jest.mock('jattac.libs.web.zest-file-upload', () => ({
  __esModule: true,
  default: ({ label, onFileSelect }: any) => (
    <input
      type="file"
      aria-label={label}
      onChange={e => { void onFileSelect(e.target.files?.[0] ?? null); }}
    />
  ),
}));

const PEM = '-----BEGIN RSA PRIVATE KEY-----\nMII-SECRET-BODY\n-----END RSA PRIVATE KEY-----\n';

const chooseFile = async (input: HTMLElement, file: File) => {
  fireEvent.change(input, { target: { files: [file] } });
  // the form reads the file asynchronously; flush the read before assertions
  await act(async () => { await Promise.resolve(); });
};

describe('KeyFileForm', () => {
  it('R5 requires a name before saving', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.click(screen.getByText('Save Key File'));

    expect(await screen.findByText('Name is required.')).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();
  });

  it('R5 requires a .pem file on create', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'prod' } });
    fireEvent.click(screen.getByText('Save Key File'));

    expect(await screen.findByText('Choose a .pem key file.')).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();
  });

  it('R5 rejects a file that is not a private key', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'prod' } });
    await chooseFile(screen.getByLabelText('Choose private key file'), new File(['hello, not a key'], 'notes.txt'));

    await waitFor(() => {
      expect(screen.getByText(/does not look like a private key/)).toBeInTheDocument();
    });
    fireEvent.click(screen.getByText('Save Key File'));
    expect(onSave).not.toHaveBeenCalled();
  });

  it('R5 rejects a file larger than 64 KB', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'prod' } });
    await chooseFile(screen.getByLabelText('Choose private key file'), new File([new Array(70000).fill('a').join('')], 'big.pem'));

    await waitFor(() => {
      expect(screen.getByText('The key file must be 64 KB or smaller.')).toBeInTheDocument();
    });
    expect(onSave).not.toHaveBeenCalled();
  });

  it('R5 saves a valid key file, and R10 never shows its content', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'prod' } });
    await chooseFile(screen.getByLabelText('Choose private key file'), new File([PEM], 'prod.pem'));

    await waitFor(() => {
      expect(screen.getByText('Selected: prod.pem')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Save Key File'));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ name: 'prod', content: PEM }));
    });
    expect(screen.queryByText(/MII-SECRET-BODY/)).not.toBeInTheDocument();
  });

  it('R6 editing the name only sends no content and keeps the stored key untouched', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(
      <KeyFileForm
        isEdit
        initial={{ name: 'old', tags: ['prod'] }}
        onSave={onSave}
        onCancel={jest.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'renamed' } });
    fireEvent.click(screen.getByText('Update Key File'));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ name: 'renamed', content: undefined }));
    });
  });

  it('R5 saving twice quickly creates only one key file', async () => {
    const onSave = jest.fn().mockReturnValue(new Promise(() => {}));
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'prod' } });
    await chooseFile(screen.getByLabelText('Choose private key file'), new File([PEM], 'prod.pem'));

    const save = screen.getByText('Save Key File');
    fireEvent.click(save);
    fireEvent.click(save);

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
  });

  it('R5 rejects a name longer than 200 characters', async () => {
    const onSave = jest.fn().mockResolvedValue(undefined);
    render(<KeyFileForm isEdit={false} onSave={onSave} onCancel={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Key file name'), { target: { value: 'a'.repeat(201) } });
    fireEvent.click(screen.getByText('Save Key File'));

    expect(await screen.findByText('Name must be 200 characters or fewer.')).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();
  });
});
