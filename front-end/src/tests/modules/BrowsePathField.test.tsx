import { render, screen, fireEvent } from '@testing-library/react';
import React from 'react';
import BrowsePathField from '../../modules/FilePicker/BrowsePathField';

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const pickerProps: any[] = [];
jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: (props: any) => {
    pickerProps.push(props);
    return (
      <div data-testid="picker">
        <span>{props.label}</span>
        <button onClick={() => props.onSelect('/picked/path')}>pick</button>
      </div>
    );
  },
}));

beforeEach(() => { pickerProps.length = 0; });

describe('BrowsePathField', () => {
  it('lets the user still type a path by hand', () => {
    const onChange = jest.fn();
    render(<BrowsePathField value="" onChange={onChange} placeholder="/key.pem" />);
    fireEvent.change(screen.getByPlaceholderText('/key.pem'), { target: { value: '/x' } });
    expect(onChange).toHaveBeenCalledWith('/x');
  });

  it('opens a local file picker and applies the chosen path', () => {
    const onChange = jest.fn();
    render(<BrowsePathField value="" onChange={onChange} placeholder="p"
      storageKey="ssh-key" label="Pick key" />);

    fireEvent.click(screen.getByText('Browse'));
    expect(screen.getByTestId('picker')).toBeTruthy();
    expect(pickerProps[0].sshConfig).toBeUndefined();
    expect(pickerProps[0].dirsOnly).toBeFalsy();
    expect(pickerProps[0].storageKey).toBe('ssh-key');

    fireEvent.click(screen.getByText('pick'));
    expect(onChange).toHaveBeenCalledWith('/picked/path');
    expect(screen.queryByTestId('picker')).toBeNull();
    expect(screen.getByPlaceholderText('p')).toBeTruthy();
  });

  it('opens a remote directory picker over SSH when sshConfig is given', () => {
    const ssh = { host: '1.2.3.4', user: 'ec2-user', keyPath: '/k.pem' };
    render(<BrowsePathField value="/home/ec2-user/app" onChange={jest.fn()} placeholder="p"
      dirsOnly sshConfig={ssh} />);

    fireEvent.click(screen.getByText('Browse'));
    expect(pickerProps[0].sshConfig).toEqual(ssh);
    expect(pickerProps[0].dirsOnly).toBe(true);
    expect(pickerProps[0].initialPath).toBe('/home/ec2-user/app');
  });

  it('disables Browse when disabled, and shows the reason', () => {
    render(<BrowsePathField value="" onChange={jest.fn()} placeholder="p"
      browseDisabled disabledReason="Fill in host, username and SSH key first." />);
    expect((screen.getByText('Browse') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Fill in host, username and SSH key first.')).toBeTruthy();
  });

  it('does not show a disabled reason when browsing is enabled', () => {
    render(<BrowsePathField value="" onChange={jest.fn()} placeholder="p" disabledReason="nope" />);
    expect(screen.queryByText('nope')).toBeNull();
  });
});
