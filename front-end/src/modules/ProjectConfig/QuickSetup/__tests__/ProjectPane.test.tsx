import { render, screen, fireEvent } from '@testing-library/react';
import ProjectPane from '../ProjectPane';
import { IProject } from '@/shared/types/IProject';

jest.mock('../QuickSetup', () => ({
  __esModule: true,
  default: ({ onManual }: any) => (
    <div>
      <span>quick setup</span>
      <button type="button" onClick={onManual}>go manual</button>
    </div>
  ),
}));

jest.mock('../../ProjectSetupWizard', () => ({
  __esModule: true,
  default: ({ existing }: any) => <div>manual wizard {existing ? `for ${existing.name}` : 'for new'}</div>,
}));

const project = { id: 'p1', name: 'Shop' } as IProject;
const KEY = 'shipright.newProjectMode';

describe('ProjectPane', () => {
  beforeEach(() => window.localStorage.clear());

  it('asks which way to create a new project before showing either flow', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);

    expect(screen.getByRole('button', { name: /^smart mode/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^legacy mode/i })).toBeInTheDocument();
    expect(screen.queryByText('quick setup')).not.toBeInTheDocument();
    expect(screen.queryByText(/manual wizard/)).not.toBeInTheDocument();
  });

  it('describes each mode in plain words and recommends Smart Mode', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);

    expect(screen.getByText(/ShipRight sets it up for you/i)).toBeInTheDocument();
    expect(screen.getByText(/fill in every setting yourself/i)).toBeInTheDocument();
    expect(screen.getByText(/recommended/i)).toBeInTheDocument();
  });

  it('Smart Mode opens the guided flow', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^smart mode/i }));

    expect(screen.getByText('quick setup')).toBeInTheDocument();
  });

  it('Legacy Mode opens the original wizard', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^legacy mode/i }));

    expect(screen.getByText('manual wizard for new')).toBeInTheDocument();
  });

  it('the guided flow can still hand over to the original wizard', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^smart mode/i }));
    fireEvent.click(screen.getByText('go manual'));

    expect(screen.getByText('manual wizard for new')).toBeInTheDocument();
  });

  it('remembers the last choice and marks it, but still shows the chooser', () => {
    const first = render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^legacy mode/i }));
    expect(window.localStorage.getItem(KEY)).toBe('legacy');
    first.unmount();

    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);

    expect(screen.getByRole('button', { name: /^smart mode/i })).toBeInTheDocument();
    expect(screen.getByText(/last used/i).closest('button')).toHaveAccessibleName(/legacy mode/i);
  });

  it('survives storage being unavailable', () => {
    const spy = jest.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    expect(screen.getByRole('button', { name: /^smart mode/i })).toBeInTheDocument();
    spy.mockRestore();
  });

  it('can be cancelled from the chooser', () => {
    const onCancel = jest.fn();
    render(<ProjectPane onSaved={jest.fn()} onCancel={onCancel} />);
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(onCancel).toHaveBeenCalled();
  });

  it('edits an existing project with the original wizard, never the chooser or the guided flow', () => {
    render(<ProjectPane existing={project} onSaved={jest.fn()} onCancel={jest.fn()} />);

    expect(screen.getByText('manual wizard for Shop')).toBeInTheDocument();
    expect(screen.queryByText('quick setup')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^smart mode/i })).not.toBeInTheDocument();
  });
});
