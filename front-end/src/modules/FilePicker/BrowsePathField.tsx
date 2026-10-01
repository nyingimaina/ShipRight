import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { ISshConfig } from '@/shared/types/IFs';
import styles from './Styles/BrowsePathField.module.css';

interface Props {
  value: string;
  onChange: (path: string) => void;
  placeholder?: string;
  /** Pick directories only (e.g. a remote working directory). */
  dirsOnly?: boolean;
  /** When set, browses the remote host over SSH instead of the local machine. */
  sshConfig?: ISshConfig;
  storageKey?: string;
  label?: string;
  browseDisabled?: boolean;
  /** Shown under the field while Browse is disabled. */
  disabledReason?: string;
}

/** A text box with a Browse button that swaps in the shared FilePicker. Typing by hand still works. */
export default function BrowsePathField({
  value, onChange, placeholder, dirsOnly, sshConfig, storageKey, label, browseDisabled, disabledReason,
}: Props) {
  const [picking, setPicking] = useState(false);

  if (picking) {
    return (
      <FilePicker
        dirsOnly={dirsOnly}
        sshConfig={sshConfig}
        storageKey={storageKey}
        label={label}
        initialPath={sshConfig ? (value || undefined) : undefined}
        onSelect={p => { onChange(p); setPicking(false); }}
      />
    );
  }

  return (
    <div>
      <div className={styles.row}>
        <ZestTextbox value={value} onChange={e => onChange(e.target.value)}
          placeholder={placeholder} zest={{ stretch: true }} />
        <ZestButton onClick={() => setPicking(true)} disabled={browseDisabled}
          zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
          Browse
        </ZestButton>
      </div>
      {browseDisabled && disabledReason && <p className={styles.hint}>{disabledReason}</p>}
    </div>
  );
}
