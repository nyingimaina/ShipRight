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

describe('ProjectPane', () => {
  it('starts a new project with the guided flow', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    expect(screen.getByText('quick setup')).toBeInTheDocument();
    expect(screen.queryByText(/manual wizard/)).not.toBeInTheDocument();
  });

  it('switches to the original wizard on request', () => {
    render(<ProjectPane onSaved={jest.fn()} onCancel={jest.fn()} />);
    fireEvent.click(screen.getByText('go manual'));
    expect(screen.getByText('manual wizard for new')).toBeInTheDocument();
  });

  it('edits an existing project with the original wizard, never the guided flow', () => {
    render(<ProjectPane existing={project} onSaved={jest.fn()} onCancel={jest.fn()} />);
    expect(screen.getByText('manual wizard for Shop')).toBeInTheDocument();
    expect(screen.queryByText('quick setup')).not.toBeInTheDocument();
  });
});
