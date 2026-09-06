import { render, screen, fireEvent } from '@testing-library/react';
import InfoTip from '../InfoTip';

describe('InfoTip', () => {
  it('renders the toggle label collapsed by default', () => {
    render(<InfoTip title="What is this?" id="tip1">Body text</InfoTip>);
    expect(screen.getByText('What is this?')).toBeInTheDocument();
    expect(screen.queryByText('Body text')).not.toBeInTheDocument();
  });

  it('expands the guidance body on click', () => {
    render(<InfoTip title="How keys work">Keys are stored encrypted.</InfoTip>);
    fireEvent.click(screen.getByRole('button'));
    expect(screen.getByText('Keys are stored encrypted.')).toBeInTheDocument();
  });

  it('collapses on second click', () => {
    render(<InfoTip title="Region">Region matters for ECR.</InfoTip>);
    const toggle = screen.getByRole('button');
    fireEvent.click(toggle);
    expect(screen.getByText('Region matters for ECR.')).toBeInTheDocument();
    fireEvent.click(toggle);
    expect(screen.queryByText('Region matters for ECR.')).not.toBeInTheDocument();
  });

  it('exposes an accessible label from id', () => {
    render(<InfoTip title="Profile" id="prof-tip">Guide</InfoTip>);
    expect(screen.getByLabelText('Help: Profile')).toBeInTheDocument();
  });
});