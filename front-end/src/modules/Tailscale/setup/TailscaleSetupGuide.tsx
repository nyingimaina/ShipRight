import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import {
  SETUP_PROGRESS_KEY,
  setupSteps,
  setupTroubleshooting,
  type SetupLink,
  type SetupSnippet,
} from './tailscaleSetup';
import styles from './TailscaleSetupGuide.module.css';

function RichText({ text }: { text: string }) {
  return (
    <>
      {text.split(/(`[^`]+`)/g).map((part, index) =>
        part.startsWith('`') && part.endsWith('`')
          ? <code key={index} className={styles.inlineCode}>{part.slice(1, -1)}</code>
          : <span key={index}>{part}</span>,
      )}
    </>
  );
}

function LinkRow({ links }: { links: SetupLink[] }) {
  return (
    <div className={styles.links}>
      {links.map(link => (
        <a
          key={link.href + link.label}
          className={styles.link}
          href={link.href}
          {...(link.internal ? {} : { target: '_blank', rel: 'noreferrer' })}
        >
          {link.label}
          {!link.internal && <span aria-hidden="true"> ↗</span>}
        </a>
      ))}
    </div>
  );
}

function Snippet({ snippet }: { snippet: SetupSnippet }) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(snippet.code);
      toast.success(`${snippet.label} copied.`);
    } catch {
      toast.error('Could not copy automatically. Select the text and copy it.');
    }
  };

  return (
    <div className={styles.snippet}>
      <div className={styles.snippetHeader}>
        <span>{snippet.label}</span>
        <button type="button" className={styles.copyButton} onClick={copy} aria-label={`Copy ${snippet.label}`}>
          Copy
        </button>
      </div>
      <pre className={styles.pre}><code>{snippet.code}</code></pre>
      {snippet.note && <p className={styles.snippetNote}>{snippet.note}</p>}
    </div>
  );
}

export default function TailscaleSetupGuide({ defaultOpen = true }: { defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  const [completed, setCompleted] = useState<Record<string, boolean>>({});

  useEffect(() => {
    try {
      const stored = window.localStorage.getItem(SETUP_PROGRESS_KEY);
      if (stored) setCompleted(JSON.parse(stored) as Record<string, boolean>);
    } catch {
      /* progress is a convenience, not stored data */
    }
  }, []);

  const toggle = (id: string) => {
    setCompleted(previous => {
      const next = { ...previous, [id]: !previous[id] };
      try { window.localStorage.setItem(SETUP_PROGRESS_KEY, JSON.stringify(next)); } catch { /* ignore */ }
      return next;
    });
  };

  const reset = () => {
    setCompleted({});
    try { window.localStorage.removeItem(SETUP_PROGRESS_KEY); } catch { /* ignore */ }
  };

  const done = setupSteps.filter(step => completed[step.id]).length;
  const percent = Math.round((done / setupSteps.length) * 100);

  return (
    <section id="tailscale-setup" className={styles.guide}>
      <button
        type="button"
        className={styles.header}
        aria-expanded={open}
        aria-controls="tailscale-setup-body"
        onClick={() => setOpen(value => !value)}
      >
        <span className={styles.headerIcon} aria-hidden="true">◎</span>
        <span className={styles.headerText}>
          <span className={styles.title}>Set up Tailscale</span>
          <span className={styles.subtitle}>
            A guided path from an empty admin console to a working ShipRight connection.
          </span>
        </span>
        <span className={styles.progressText}>{done} of {setupSteps.length}</span>
        <span className={styles.chevron} aria-hidden="true">{open ? '▾' : '▸'}</span>
      </button>

      <div className={styles.progressTrack} aria-hidden="true">
        <span className={styles.progressFill} style={{ width: `${percent}%` }} />
      </div>

      {open && (
        <div id="tailscale-setup-body" className={styles.body}>
          <ol className={styles.steps}>
            {setupSteps.map((step, index) => (
              <li key={step.id} className={completed[step.id] ? styles.stepDone : styles.step}>
                <div className={styles.stepTop}>
                  <span className={styles.number} aria-hidden="true">
                    {completed[step.id] ? '✓' : index + 1}
                  </span>
                  <div className={styles.stepHeading}>
                    <h3>{step.title}</h3>
                    <p>{step.summary}</p>
                  </div>
                  <label className={styles.doneToggle}>
                    <input
                      type="checkbox"
                      checked={!!completed[step.id]}
                      onChange={() => toggle(step.id)}
                      aria-label={`Mark step ${index + 1} done: ${step.title}`}
                    />
                    <span>Done</span>
                  </label>
                </div>

                <div className={styles.stepBody}>
                  {step.details.map((detail, detailIndex) => (
                    <p key={detailIndex}><RichText text={detail} /></p>
                  ))}

                  {step.warning && <p className={styles.warning}>{step.warning}</p>}
                  {step.links && <LinkRow links={step.links} />}
                  {step.snippets?.map(snippet => <Snippet key={snippet.label} snippet={snippet} />)}

                  {step.alternatives?.map(alternative => (
                    <details key={alternative.title} className={styles.alternative}>
                      <summary>{alternative.title}</summary>
                      {alternative.details.map((detail, detailIndex) => (
                        <p key={detailIndex}><RichText text={detail} /></p>
                      ))}
                      {alternative.links && <LinkRow links={alternative.links} />}
                      {alternative.snippets?.map(snippet => <Snippet key={snippet.label} snippet={snippet} />)}
                    </details>
                  ))}
                </div>
              </li>
            ))}
          </ol>

          <div className={styles.troubleshooting}>
            <h3>Troubleshooting</h3>
            {setupTroubleshooting.map(item => (
              <div key={item.symptom} className={styles.troubleRow}>
                <div className={styles.troubleSymptom}>{item.symptom}</div>
                <div className={styles.troubleFix}><RichText text={item.fix} /></div>
                {item.links && <LinkRow links={item.links} />}
              </div>
            ))}
          </div>

          <div className={styles.footer}>
            <span>{done === setupSteps.length ? 'All steps done. Nice.' : 'Progress is saved in this browser.'}</span>
            {done > 0 && (
              <button type="button" className={styles.reset} onClick={reset}>Reset progress</button>
            )}
          </div>
        </div>
      )}
    </section>
  );
}
