import { useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { runZestAction } from '@/shared/components/ZestAsync/zestAction';
import type { IHostSwap } from './IHostSwap';
import styles from './AppHeader.module.css';

interface Props {
  swap: IHostSwap | null;
  canCreate: boolean;
  onChanged: () => void;
}

const SIZE_GB = 2;

/**
 * Shows host-metal swap and, when none exists, lets an Admin create it on the metal over SSH.
 * The request is deliberately behind a second confirmation; it must never run swapon in this container.
 */
export default function HostSwapMetric({ swap, canCreate, onChanged }: Props) {
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState('');
  const errorRef = useRef<HTMLSpanElement>(null);

  if (!swap || !swap.available) return <span className={styles.metric}>Swap —</span>;

  if (swap.swapTotalBytes > 0) {
    return <span className={styles.metric}>Swap {Math.round(swap.swapUsedPercent)}%</span>;
  }

  const create = async () => {
    const result = await runZestAction(async () => {
      const response = await api.post<{ success: boolean; output: string }>('/api/system/host-swap', { sizeGb: SIZE_GB });
      if (!response.success) throw new Error(response.output || 'Swap creation failed.');
      return response;
    }, { fallbackMessage: 'Swap creation failed.', onError: setError, errorRef });
    toast.success('Swap enabled on the host.');
    setConfirming(false);
    onChanged();
    return result;
  };

  if (!canCreate || !swap.configured) return <span className={styles.metric}>Swap 0%</span>;

  if (!confirming) {
    return (
      <button type="button" className={styles.swapAction}
        onClick={() => { setError(''); setConfirming(true); }}>
        Swap 0% · add
      </button>
    );
  }

  return (
    <span className={styles.swapPrompt}>
      Create {SIZE_GB} GB swap on the host?
      <ZestButton onClick={create} zest={{ semanticType: 'save', visualOptions: { variant: 'standard' } }}>
        Create swap
      </ZestButton>
      <ZestButton onClick={() => { setError(''); setConfirming(false); }} zest={{ buttonStyle: 'outline' }}>
        Cancel
      </ZestButton>
      {error && <span ref={errorRef} role="alert" className={styles.swapError}>{error}</span>}
    </span>
  );
}
