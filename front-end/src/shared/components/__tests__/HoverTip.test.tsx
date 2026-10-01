import { render, screen, fireEvent } from '@testing-library/react';
import React from 'react';
import HoverTip from '../HoverTip';

describe('HoverTip', () => {
  it('is hidden until hovered', () => {
    render(<HoverTip label="ECR registry">Where to find it</HoverTip>);
    expect(screen.queryByRole('tooltip')).toBeNull();
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Help: ECR registry' }));
    expect(screen.getByRole('tooltip').textContent).toContain('Where to find it');
  });

  it('hides again on mouse leave', () => {
    render(<HoverTip label="x">tip</HoverTip>);
    const btn = screen.getByRole('button', { name: 'Help: x' });
    fireEvent.mouseEnter(btn);
    fireEvent.mouseLeave(btn);
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('works from the keyboard: shows on focus, hides on blur and Escape', () => {
    render(<HoverTip label="x">tip</HoverTip>);
    const btn = screen.getByRole('button', { name: 'Help: x' });
    fireEvent.focus(btn);
    expect(screen.getByRole('tooltip')).toBeTruthy();
    fireEvent.keyDown(btn, { key: 'Escape' });
    expect(screen.queryByRole('tooltip')).toBeNull();
    fireEvent.focus(btn);
    fireEvent.blur(btn);
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('toggles on tap/click so it works on touch screens', () => {
    render(<HoverTip label="x">tip</HoverTip>);
    const btn = screen.getByRole('button', { name: 'Help: x' });
    fireEvent.click(btn);
    expect(screen.getByRole('tooltip')).toBeTruthy();
    fireEvent.click(btn);
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('links the button to the tooltip for screen readers', () => {
    render(<HoverTip label="x">tip</HoverTip>);
    const btn = screen.getByRole('button', { name: 'Help: x' });
    fireEvent.mouseEnter(btn);
    expect(btn.getAttribute('aria-describedby')).toBe(screen.getByRole('tooltip').id);
  });
});
