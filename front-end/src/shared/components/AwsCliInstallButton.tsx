import { useEffect, useState } from 'react';
import { api } from '@/shared/ApiService';
import type { IAwsCliInstallResult } from '@/shared/types/IProject';

export interface AwsCliInstallButtonProps {
  onInstalled?: () => void;
}

const buttonStyle: React.CSSProperties = {
  marginTop: 8,
  background: 'rgba(120,160,214,0.14)',
  color: '#AFC6E4',
  border: '1px solid rgba(120,160,214,0.5)',
  borderRadius: 6,
  padding: '4px 10px',
  fontSize: 12,
  cursor: 'pointer',
};

const codeStyle: React.CSSProperties = {
  display: 'block',
  marginTop: 6,
  background: 'rgba(0,0,0,0.35)',
  border: '1px solid rgba(255,255,255,0.12)',
  borderRadius: 6,
  padding: '6px 8px',
  fontSize: 11,
  color: '#E8EEF6',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-all',
};

/**
 * Lets the user install the AWS CLI on the build machine (native / WSL / SSH —
 * decided by the server) right from the validation panel. When sudo is required
 * but unavailable it falls back to a no-sudo user-level pip install.
 */
export default function AwsCliInstallButton({ onInstalled }: AwsCliInstallButtonProps) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<IAwsCliInstallResult | null>(null);

  useEffect(() => {
    if (result?.success) onInstalled?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [result]);

  const install = async (strategy: 'auto' | 'pip') => {
    setBusy(true);
    try {
      setResult(await api.post<IAwsCliInstallResult>('/api/resources/aws-profiles/install-cli', { strategy }));
    } catch {
      setResult({ success: false, message: 'Could not reach the server to install the AWS CLI.' });
    } finally {
      setBusy(false);
    }
  };

  const manual = result && !result.success && result.requiresManualInstall && result.command;
  const failed = result && !result.success && !result.requiresManualInstall;

  return (
    <div style={{ marginTop: 4 }}>
      <button type="button" style={buttonStyle} disabled={busy} onClick={() => install('auto')}>
        {busy ? 'Installing…' : 'Install AWS CLI'}
      </button>

      {result?.success && (
        <div role="status" style={{ marginTop: 6, fontSize: 12, color: '#7CD9A8' }}>
          ✓ {result.message}
        </div>
      )}

      {manual && (
        <div style={{ marginTop: 6, fontSize: 12, color: '#E0A63C' }}>
          {result.message}
          <code style={codeStyle}>{result.command}</code>
          <button type="button" style={buttonStyle} disabled={busy} onClick={() => install('pip')}>
            {busy ? 'Installing…' : 'Try without sudo (pip)'}
          </button>
        </div>
      )}

      {failed && (
        <div role="alert" style={{ marginTop: 6, fontSize: 12, color: '#E06060' }}>
          {result.message}
          {result.outputTail && <code style={codeStyle}>{result.outputTail}</code>}
        </div>
      )}
    </div>
  );
}