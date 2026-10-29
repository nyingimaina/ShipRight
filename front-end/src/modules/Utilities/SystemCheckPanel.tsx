import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { CheckStatus, ISystemCheckItem, ISystemCheckReport, ISystemFixResult } from '@/shared/types/IDiskCleanup';
import styles from './SystemCheckPanel.module.css';

const STATUS_TEXT: Record<CheckStatus, string> = {
  Ok: 'OK', Warning: 'Needs attention', Problem: 'Problem', Skipped: 'Waiting',
};

const goToDiskCleanup = () => document.getElementById('disk-cleanup')?.scrollIntoView({ behavior: 'smooth', block: 'start' });

export default function SystemCheckPanel() {
  const [report, setReport] = useState<ISystemCheckReport | null>(null);
  const [failed, setFailed] = useState(false);
  const [fixing, setFixing] = useState<string | null>(null);
  const [last, setLast] = useState<{ ok: boolean; message: string; output: string } | null>(null);

  const check = useCallback(async () => {
    setFailed(false);
    try {
      setReport(await api.get<ISystemCheckReport>('/api/system/check'));
    } catch {
      setFailed(true);
    }
  }, []);

  useEffect(() => { check(); }, [check]);

  const fix = async (item: ISystemCheckItem) => {
    if (!item.fixId) return;
    setFixing(item.fixId);
    setLast(null);
    try {
      const res = await api.post<ISystemFixResult>('/api/system/check/fix', { fixId: item.fixId });
      setReport(res.report);
      setLast({ ok: res.succeeded, message: res.message, output: res.output });
      (res.succeeded ? toast.success : toast.error)(res.message);
    } catch (e) {
      const message = (e as { message?: string })?.message ?? 'The repair could not be started.';
      setLast({ ok: false, message, output: '' });
      toast.error(message);
    } finally {
      setFixing(null);
    }
  };

  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      toast.success('Copied.');
    } catch {
      toast.error('Could not copy. Select the text and copy it by hand.');
    }
  };

  const attention = report?.items.filter(i => i.status === 'Problem' || i.status === 'Warning').length ?? 0;

  return (
    <section className={styles.panel} aria-label="System check">
      <div className={styles.head}>
        <div>
          <h2 className={styles.title}>System check</h2>
          <p className={styles.summary}>
            {failed
              ? 'ShipRight could not run the check.'
              : !report ? 'Looking at your computer…'
              : attention === 0 ? 'Everything looks good.'
              : `${attention} thing${attention === 1 ? '' : 's'} need${attention === 1 ? 's' : ''} attention.`}
          </p>
        </div>
        <ZestButton onClick={check} disabled={fixing !== null} zest={{ buttonStyle: 'outline' }}>Check again</ZestButton>
      </div>

      <ul className={styles.list}>
        {report?.items.map(i => (
          <li key={i.id} className={`${styles.item} ${styles[`s${i.status}`]}`}>
            <div className={styles.itemMain}>
              <span className={styles.chip}>{STATUS_TEXT[i.status]}</span>
              <div className={styles.itemText}>
                <strong>{i.title}</strong>
                <span className={styles.detail}>{i.detail}</span>
              </div>
            </div>

            {(i.fixId || i.manualSteps) && (
              <div className={styles.itemActions}>
                {i.fixId && (
                  <ZestButton onClick={() => fix(i)} disabled={fixing !== null} zest={{ visualOptions: { variant: 'standard' } }}>
                    {i.fixLabel ?? 'Fix it'}
                  </ZestButton>
                )}
                {i.id === 'disk' && i.manualSteps && (
                  <ZestButton onClick={goToDiskCleanup} zest={{ visualOptions: { variant: 'standard' } }}>Go to disk clean-up</ZestButton>
                )}
                {i.id !== 'disk' && i.manualSteps && (
                  <>
                    <pre className={styles.steps}>{i.manualSteps}</pre>
                    <ZestButton onClick={() => copy(i.manualSteps!)} zest={{ buttonStyle: 'outline' }}>Copy the steps</ZestButton>
                  </>
                )}
                {fixing === i.fixId && (
                  <span className={styles.working}>Working… this can take several minutes. Please keep this window open.</span>
                )}
              </div>
            )}
          </li>
        ))}
      </ul>

      {last && (
        <div className={styles.result}>
          <p className={last.ok ? styles.ok : styles.warn}>{last.message}</p>
          {last.output && <pre className={styles.output}>{last.output}</pre>}
        </div>
      )}
    </section>
  );
}
