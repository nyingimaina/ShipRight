import { useRef, useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import ZestFileUpload from 'jattac.libs.web.zest-file-upload';
import TagsInput from '@/shared/components/TagsInput';
import styles from '@/pages/Styles/Resources.module.css';

export const MAX_KEY_FILE_NAME = 200;
export const MAX_KEY_FILE_BYTES = 64 * 1024;

export interface KeyFileFormValues {
  name: string;
  tags?: string[];
  /** The .pem text, present only when a new file was chosen. Never shown back to the user. */
  content?: string;
}

export interface KeyFileFormErrors {
  name?: string;
  content?: string;
}

interface Props {
  initial?: { name: string; tags?: string[] };
  isEdit?: boolean;
  allTags?: string[];
  /** Field errors the server returned beside a field (RFC 7807). */
  externalErrors?: KeyFileFormErrors;
  onSave: (values: KeyFileFormValues) => Promise<void>;
  onCancel: () => void;
}

const readAsText = (file: File): Promise<string> => {
  if (typeof (file as any).text === 'function') return (file as any).text();
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result ?? ''));
    reader.onerror = () => reject(reader.error ?? new Error('read failed'));
    reader.readAsText(file);
  });
};

/**
 * Add or edit a saved .pem key file (R5, R6). The file content is read into memory only to send it
 * once; it is never rendered back and the form never shows a stored key (R10).
 */
export default function KeyFileForm({ initial, isEdit, allTags = [], externalErrors, onSave, onCancel }: Props) {
  const [name, setName] = useState(initial?.name ?? '');
  const [tags, setTags] = useState<string[]>(initial?.tags ?? []);
  const [content, setContent] = useState<string | undefined>(undefined);
  const [fileName, setFileName] = useState('');
  const [errors, setErrors] = useState<KeyFileFormErrors>({});
  const [saving, setSaving] = useState(false);
  const inFlight = useRef(false);

  const shownErrors: KeyFileFormErrors = { ...externalErrors, ...errors };

  const handleFile = async (file: File | null) => {
    if (!file) {
      setContent(undefined);
      setFileName('');
      return;
    }
    setFileName(file.name);
    if (file.size > MAX_KEY_FILE_BYTES) {
      setContent(undefined);
      setErrors(e => ({ ...e, content: 'The key file must be 64 KB or smaller.' }));
      return;
    }
    try {
      const text = await readAsText(file);
      if (!text.includes('-----BEGIN') || !text.includes('PRIVATE KEY-----')) {
        setContent(undefined);
        setErrors(e => ({ ...e, content: 'That file does not look like a private key (.pem). Choose the key file Amazon gave you.' }));
        return;
      }
      setContent(text);
      setErrors(e => ({ ...e, content: undefined }));
    } catch {
      setContent(undefined);
      setErrors(e => ({ ...e, content: 'The file could not be read. Choose an unprotected .pem key file.' }));
    }
  };

  const validate = (): KeyFileFormErrors => {
    const next: KeyFileFormErrors = {};
    const trimmed = name.trim();
    if (!trimmed) next.name = 'Name is required.';
    else if (trimmed.length > MAX_KEY_FILE_NAME) next.name = `Name must be ${MAX_KEY_FILE_NAME} characters or fewer.`;
    if (!isEdit && !content) next.content = 'Choose a .pem key file.';
    return next;
  };

  const handleSubmit = async () => {
    if (inFlight.current) return;
    const next = validate();
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    inFlight.current = true;
    setSaving(true);
    try {
      await onSave({ name: name.trim(), tags, content });
    } finally {
      inFlight.current = false;
      setSaving(false);
    }
  };

  return (
    <div>
      <div className={styles.formRow}>
        <label className={styles.label} htmlFor="key-file-name">
          Name <span style={{ color: '#C9A84C' }}>*</span>
        </label>
        <ZestTextbox
          id="key-file-name"
          aria-label="Key file name"
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="e.g. EC2 production key"
          zest={{ stretch: true }}
        />
        {shownErrors.name && (
          <p role="alert" style={{ margin: '4px 0 0', fontSize: 12, color: '#FF6B6B' }}>{shownErrors.name}</p>
        )}
      </div>

      <div className={styles.formRow}>
        <label className={styles.label}>
          Private key file (.pem) {!isEdit && <span style={{ color: '#C9A84C' }}>*</span>}
        </label>
        <ZestFileUpload
          label={isEdit ? 'Replace private key file' : 'Choose private key file'}
          accept=".pem,.key,.txt"
          maxFileSize={MAX_KEY_FILE_BYTES}
          hideCompletionMessage
          onFileSelect={handleFile}
          onError={message => setErrors(e => ({ ...e, content: message }))}
        />
        {fileName && <p className={styles.cardDetail}>Selected: {fileName}</p>}
        {isEdit && !fileName && (
          <p className={styles.cardDetail}>Leave empty to keep the stored key; choose a file to replace it.</p>
        )}
        {shownErrors.content && (
          <p role="alert" style={{ margin: '4px 0 0', fontSize: 12, color: '#FF6B6B' }}>{shownErrors.content}</p>
        )}
      </div>

      <div className={styles.formRow}>
        <label className={styles.label}>Tags</label>
        <TagsInput value={tags} onChange={setTags} allTags={allTags} id="ckeyfile-tags" />
      </div>

      <div className={styles.footer}>
        <ZestButton
          onClick={handleSubmit}
          disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}
        >
          {saving ? 'Saving…' : isEdit ? 'Update Key File' : 'Save Key File'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}
