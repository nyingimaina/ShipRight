import { useRef, useState } from 'react';
import toast from 'react-hot-toast';
import { api } from '@/shared/ApiService';
import Spinner from '@/shared/components/Busy/Spinner';
import styles from './Styles/SwapOffer.module.css';

interface Props {
  serverId: string;
  reachable: boolean;
  swapTotalMb: number;
  memTotalMb: number;
  /** Called with the size created, so the page can show it without waiting for the next reading. */
  onCreated: (sizeGb: number) => void;
}

const SIZE_GB = 2;

/** Shown on the Stats tab when a server has no swap: one click creates a persistent swap file. */
export default function SwapOffer({ serverId, reachable, swapTotalMb, memTotalMb, onCreated }: Props) {
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const inFlight = useRef(false);

  if (!reachable || swapTotalMb > 0) return null;

  const create = async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setProblem(null);
    try {
      const r = await api.post<{ success: boolean; output: string }>(`/api/servers/${serverId}/swap`, { sizeGb: SIZE_GB });
      if (r.success) {
        toast.success(`${SIZE_GB} GB of swap created.`);
        onCreated(SIZE_GB);
      } else {
        setProblem(r.output || 'The server could not create swap.');
      }
    } catch (e) {
      setProblem((e as { message?: string })?.message ?? 'Could not reach the server.');
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  };

  return (
    <div className={styles.box}>
      <p className={styles.text}>
        <strong>This server has no swap.</strong> It has {memTotalMb} MB of memory. When memory runs out, programs
        such as the database get killed or the whole server freezes. Swap is spare disk space the server can use as
        emergency memory, and it is recommended on small servers.
      </p>
      {busy
        ? <Spinner label="Creating swap…" hint="This takes a few seconds." />
        : null}
      <button type="button" className={styles.btn} onClick={create} disabled={busy}
        title={`Creates a ${SIZE_GB} GB file on the server's disk and turns it on. It stays on after a reboot.`}>
        Create {SIZE_GB} GB swap
      </button>
      {problem && <p className={styles.problem}>{problem}</p>}
    </div>
  );
}
