import { useId, useState } from 'react';

interface HoverTipProps {
  /** Name of the field this tip explains; used for the accessible button name. */
  label: string;
  children: React.ReactNode;
}

const wrapStyle: React.CSSProperties = { position: 'relative', display: 'inline-flex', marginLeft: 6, verticalAlign: 'middle' };

const buttonStyle: React.CSSProperties = {
  width: 16, height: 16, borderRadius: '50%', padding: 0, lineHeight: '14px',
  background: 'rgba(201,168,76,0.12)', color: '#C9A84C', border: '1px solid rgba(201,168,76,0.5)',
  fontSize: 11, fontWeight: 700, cursor: 'help', fontFamily: 'inherit',
};

const bubbleStyle: React.CSSProperties = {
  position: 'absolute', top: '100%', left: 0, marginTop: 6, zIndex: 20,
  width: 300, maxWidth: '80vw', padding: '8px 10px', borderRadius: 6,
  background: '#1B2740', border: '1px solid rgba(201,168,76,0.4)', color: '#E3E9F2',
  fontSize: 12, lineHeight: 1.5, fontWeight: 400, whiteSpace: 'pre-line',
  boxShadow: '0 6px 18px rgba(0,0,0,0.4)',
};

/**
 * A small "?" that explains a field in plain language. Shows on hover and keyboard focus,
 * and toggles on tap for touch screens. Use it next to labels of fields that a
 * non-technical user would not understand or know where to find the value for.
 */
export default function HoverTip({ label, children }: HoverTipProps) {
  const [hovered, setHovered] = useState(false);
  const [pinned, setPinned] = useState(false);
  const id = useId();
  const open = hovered || pinned;

  const close = () => { setHovered(false); setPinned(false); };

  return (
    <span style={wrapStyle}>
      <button
        type="button"
        style={buttonStyle}
        aria-label={`Help: ${label}`}
        aria-describedby={open ? id : undefined}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        onFocus={() => setHovered(true)}
        onBlur={close}
        onClick={() => setPinned(p => !p)}
        onKeyDown={e => { if (e.key === 'Escape') close(); }}
      >
        ?
      </button>
      {open && <span id={id} role="tooltip" style={bubbleStyle}>{children}</span>}
    </span>
  );
}
