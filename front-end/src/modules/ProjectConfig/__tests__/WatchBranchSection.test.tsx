import { render, screen, fireEvent } from '@testing-library/react';
import WatchBranchSection from '../WatchBranchSection';

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const defaultFields = { watchBranch: undefined, watchPollSeconds: 300, watchSteps: 'Build' };

describe('WatchBranchSection', () => {
  it('shows enable checkbox unchecked when watchBranch is undefined', () => {
    const onChange = jest.fn();
    render(<WatchBranchSection fields={defaultFields} onChange={onChange} />);
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).not.toBeChecked();
  });

  it('hides branch/poll/steps fields when disabled', () => {
    const onChange = jest.fn();
    render(<WatchBranchSection fields={defaultFields} onChange={onChange} />);
    expect(screen.queryByPlaceholderText('master')).not.toBeInTheDocument();
  });

  it('shows branch/poll/steps fields when enabled', () => {
    const onChange = jest.fn();
    const fields = { watchBranch: 'master', watchPollSeconds: 300, watchSteps: 'Build' };
    render(<WatchBranchSection fields={fields} onChange={onChange} />);
    expect(screen.getByPlaceholderText('master')).toBeInTheDocument();
    expect(screen.getByDisplayValue('5 minutes')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Build only')).toBeInTheDocument();
  });

  it('calls onChange with watchBranch="master" when checkbox is checked', () => {
    const onChange = jest.fn();
    render(<WatchBranchSection fields={defaultFields} onChange={onChange} />);
    const checkbox = screen.getByRole('checkbox');
    fireEvent.click(checkbox);
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({ watchBranch: 'master' })
    );
  });

  it('calls onChange with watchBranch=undefined when checkbox is unchecked', () => {
    const onChange = jest.fn();
    const fields = { watchBranch: 'main', watchPollSeconds: 300, watchSteps: 'Build' };
    render(<WatchBranchSection fields={fields} onChange={onChange} />);
    const checkbox = screen.getByRole('checkbox');
    fireEvent.click(checkbox);
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({ watchBranch: undefined })
    );
  });

  it('calls onChange with updated branch name when text changes', () => {
    const onChange = jest.fn();
    const fields = { watchBranch: 'master', watchPollSeconds: 300, watchSteps: 'Build' };
    render(<WatchBranchSection fields={fields} onChange={onChange} />);
    const input = screen.getByPlaceholderText('master');
    fireEvent.change(input, { target: { value: 'main' } });
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({ watchBranch: 'main' })
    );
  });

  it('calls onChange with updated watchSteps when steps selector changes', () => {
    const onChange = jest.fn();
    const fields = { watchBranch: 'master', watchPollSeconds: 300, watchSteps: 'Build' };
    render(<WatchBranchSection fields={fields} onChange={onChange} />);
    const selects = screen.getAllByRole('combobox');
    const stepsSelect = selects[1]; // poll interval is first, steps is second
    fireEvent.change(stepsSelect, { target: { value: 'BuildAndPush' } });
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({ watchSteps: 'BuildAndPush' })
    );
  });
});
