import { render, screen } from '@testing-library/react';
import React from 'react';
import Spinner from '../Spinner';

describe('Spinner', () => {
  it('is an accessible status with the given label', () => {
    render(<Spinner label="Connecting to the server…" />);
    const status = screen.getByRole('status');
    expect(status).toHaveTextContent('Connecting to the server…');
  });

  it('can explain a slow wait with a hint', () => {
    render(<Spinner label="Loading…" hint="Slow connections can take a few seconds." />);
    expect(screen.getByText('Slow connections can take a few seconds.')).toBeInTheDocument();
  });

  it('defaults the label', () => {
    render(<Spinner />);
    expect(screen.getByRole('status')).toHaveTextContent('Loading…');
  });
});
