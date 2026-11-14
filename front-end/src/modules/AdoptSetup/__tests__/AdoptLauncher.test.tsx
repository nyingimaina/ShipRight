import { render, screen, fireEvent } from '@testing-library/react';
import AdoptLauncher from '../AdoptLauncher';

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick }: any) => <button type="button" onClick={onClick}>{children}</button>,
}));
jest.mock('../AdoptSetup', () => ({
  __esModule: true,
  default: ({ onSaved, onCancel }: any) => (
    <div>
      <button type="button" onClick={onCancel}>cancel adopt</button>
      <button type="button" onClick={() => onSaved({ id: 'p1' })}>save adopt</button>
    </div>
  ),
}));

describe('AdoptLauncher', () => {
  it('shows a button, and opens the adoption flow in a dialog from it', () => {
    render(<AdoptLauncher />);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /adopt docker setup/i }));
    expect(screen.getByRole('dialog', { name: /adopt a project/i })).toBeInTheDocument();
  });

  it('closes on cancel', () => {
    render(<AdoptLauncher />);
    fireEvent.click(screen.getByRole('button', { name: /adopt docker setup/i }));
    fireEvent.click(screen.getByRole('button', { name: 'cancel adopt' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('closes and tells the page to reload when a project was created', () => {
    const onSaved = jest.fn();
    render(<AdoptLauncher onSaved={onSaved} />);
    fireEvent.click(screen.getByRole('button', { name: /adopt docker setup/i }));
    fireEvent.click(screen.getByRole('button', { name: 'save adopt' }));
    expect(onSaved).toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
