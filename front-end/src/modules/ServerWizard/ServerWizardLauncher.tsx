import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ServerWizard from './ServerWizard';

export const FIX_CONNECTION_EVENT = 'shipright:fix-server-connection';

/** Asks the launcher (wherever it is on the page) to open the wizard on a saved server, to fix or re-test its connection. */
export function requestConnectionFix(serverId: string) {
  window.dispatchEvent(new CustomEvent<string>(FIX_CONNECTION_EVENT, { detail: serverId }));
}

interface Props {
  /** Called after a server was connected, so the page can reload its list. */
  onFinished?: () => void;
}

/** The "Guided setup" button, and the wizard it opens. Lives beside the existing New Server button, which keeps working as before. */
export default function ServerWizardLauncher({ onFinished }: Props) {
  const [open, setOpen] = useState<{ existingId?: string } | null>(null);

  useEffect(() => {
    const handler = (e: Event) => setOpen({ existingId: (e as CustomEvent<string>).detail });
    window.addEventListener(FIX_CONNECTION_EVENT, handler);
    return () => window.removeEventListener(FIX_CONNECTION_EVENT, handler);
  }, []);

  return (
    <>
      <ZestButton onClick={() => setOpen({})} zest={{ buttonStyle: 'outline' }}>Guided setup</ZestButton>
      {open && (
        <ServerWizard
          key={open.existingId ?? 'new'}
          existingServerId={open.existingId}
          onClose={() => setOpen(null)}
          onFinished={() => { setOpen(null); toast.success('The server is connected.'); onFinished?.(); }}
        />
      )}
    </>
  );
}
