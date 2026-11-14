import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import AdoptSetup from './AdoptSetup';

interface Props {
  /** Called after a project was created, so the page can reload its list. */
  onSaved?: () => void;
}

const overlay: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', zIndex: 1000,
  display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16,
};
const dialog: React.CSSProperties = {
  width: 680, maxWidth: '100%', maxHeight: '92vh', overflowY: 'auto', borderRadius: 10,
  background: 'var(--bg-card, #16203A)', border: '1px solid var(--border-subtle)', padding: '18px 22px',
};

/** The "Adopt Docker setup" button and the dialog it opens. Sits beside New Project, which keeps working as before. */
export default function AdoptLauncher({ onSaved }: Props) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <ZestButton onClick={() => setOpen(true)} zest={{ buttonStyle: 'outline' }}>Adopt Docker setup</ZestButton>
      {open && (
        <div style={overlay}>
          <div role="dialog" aria-modal="true" aria-label="Adopt a project that already has Docker files" style={dialog}>
            <AdoptSetup onCancel={() => setOpen(false)} onSaved={() => { setOpen(false); onSaved?.(); }} />
          </div>
        </div>
      )}
    </>
  );
}
