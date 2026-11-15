import { useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import HoverTip from '@/shared/components/HoverTip';

interface Props {
  /** What is being copied, used for the button's accessible name. */
  label: string;
  text: string;
  /** Plain-words explanation of where to run it. Defaults to "on the server". */
  where?: string;
  tip?: string;
}

const box: React.CSSProperties = {
  marginTop: 8, borderRadius: 6, border: '1px solid var(--border-subtle)', background: 'rgba(255,255,255,0.03)',
};
const pre: React.CSSProperties = {
  margin: 0, padding: '10px 12px', fontSize: 12, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
  whiteSpace: 'pre-wrap', wordBreak: 'break-all', userSelect: 'all',
};
/** These screens track their own busy state, so ZestButton must not also lock itself after a click. */
const NOBUSY = { busyOptions: { handleInternally: false } };

/** A command the user copies and runs themselves, always with where to run it. ShipRight never runs it. */
export default function CopyBox({ label, text, where = 'Run this on the server', tip }: Props) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      toast.success('Copied.');
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 2000);
    } catch {
      toast.error('Could not copy automatically. Select the text and copy it by hand.');
    }
  };

  return (
    <div style={box}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '6px 12px', borderBottom: '1px solid var(--border-subtle)' }}>
        <span style={{ fontSize: 12, fontWeight: 600 }}>
          {where}
          {tip && <HoverTip label={label}>{tip}</HoverTip>}
        </span>
        <ZestButton onClick={copy} aria-label={`Copy ${label}`} zest={{ ...NOBUSY, semanticType: 'copy', buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>{copied ? 'Copied ✓' : 'Copy'}</ZestButton>
      </div>
      <pre style={pre}>{text}</pre>
    </div>
  );
}
