import { useState } from 'react';

export interface InfoTipProps {
  title: string;
  id?: string;
  children: React.ReactNode;
}

const toggleStyle: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 6,
  background: 'rgba(99,115,137,0.15)',
  color: '#9FB4CD',
  border: '1px solid rgba(255,255,255,0.10)',
  borderRadius: 6,
  padding: '3px 8px',
  fontSize: 11,
  cursor: 'pointer',
  fontFamily: 'inherit',
};

const iconStyle: React.CSSProperties = {
  color: '#C9A84C',
  fontSize: 12,
  lineHeight: 1,
};

const bodyStyle: React.CSSProperties = {
  marginTop: 6,
  background: 'rgba(201,168,76,0.07)',
  border: '1px solid rgba(201,168,76,0.25)',
  borderLeft: '3px solid rgba(201,168,76,0.7)',
  borderRadius: 4,
  padding: '8px 10px',
  fontSize: 12,
  color: '#C7D2E0',
  lineHeight: 1.45,
  whiteSpace: 'pre-line',
};

/**
 * Small expandable guidance chip used to help non-expert users understand
 * a field without cluttering the rest of the form.
 */
export default function InfoTip({ title, id, children }: InfoTipProps) {
  const [open, setOpen] = useState(false);

  return (
    <div style={{ marginTop: 6 }}>
      <button
        type="button"
        style={toggleStyle}
        onClick={() => setOpen(o => !o)}
        aria-expanded={open}
        aria-label={id ? `Help: ${title}` : 'Help'}
      >
        <span style={iconStyle} aria-hidden="true">?</span>
        {title}
        <span aria-hidden="true" style={{ fontSize: 9 }}>{open ? '▲' : '▼'}</span>
      </button>
      {open && (
        <div style={bodyStyle} role="region">
          {children}
        </div>
      )}
    </div>
  );
}