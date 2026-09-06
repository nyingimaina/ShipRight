import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import type { IAwsCredentialsSource, IAwsCredentialProfile, IAwsProfileValidationResult } from '@/shared/types/IProject';
import TagsInput from '@/shared/components/TagsInput';
import InfoTip from '@/shared/components/InfoTip';
import AwsCliInstallButton from '@/shared/components/AwsCliInstallButton';
import { guidedErrorFor } from '@/shared/awsGuided';
import { useServerMode } from '@/shared/serverInfo';

export type AwsProfileWizardInput = {
  id?: string;
  name: string;
  profileName?: string;
  accessKeyId?: string;
  secretAccessKey?: string;
  sessionToken?: string;
  defaultRegion?: string;
  tags?: string[];
};

interface AwsProfileWizardProps {
  initial: AwsProfileWizardInput;
  isEdit: boolean;
  allTags: string[];
  onSave: (a: AwsProfileWizardInput) => Promise<void>;
  onCancel: () => void;
}

const commonAwsRegions = [
  'us-east-1', 'us-east-2', 'us-west-1', 'us-west-2',
  'eu-west-1', 'eu-west-2', 'eu-west-3', 'eu-central-1',
  'ap-southeast-1', 'ap-southeast-2', 'ap-northeast-1', 'ap-south-1',
  'sa-east-1', 'ca-central-1',
];

const selectStyle: React.CSSProperties = {
  width: '100%', background: '#131D30', color: '#F0F2F5',
  border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
  padding: '8px 12px', fontSize: 14,
};

const stepPillStyle = (active: boolean, done: boolean): React.CSSProperties => ({
  flex: 1,
  textAlign: 'center',
  fontSize: 11,
  padding: '4px 2px',
  borderRadius: 4,
  cursor: active ? 'default' : 'pointer',
  background: active ? 'rgba(201,168,76,0.25)' : done ? 'rgba(99,115,137,0.25)' : 'rgba(99,115,137,0.12)',
  color: active ? '#F0F2F5' : done ? '#C9A84C' : '#8EA0B8',
  border: `1px solid ${active ? 'rgba(201,168,76,0.6)' : 'rgba(255,255,255,0.08)'}`,
});

const boxStyle: React.CSSProperties = {
  background: 'rgba(255,255,255,0.02)',
  border: '1px solid rgba(255,255,255,0.08)',
  borderRadius: 8,
  padding: 12,
  marginTop: 10,
};

export default function AwsProfileWizard({
  initial,
  isEdit,
  allTags,
  onSave,
  onCancel,
}: AwsProfileWizardProps) {
  const [step, setStep] = useState(1);
  const [form, setForm] = useState(initial);
  const [mode, setMode] = useState<'profile' | 'keys'>(() => (initial.accessKeyId ? 'keys' : 'profile'));
  const [source, setSource] = useState<IAwsCredentialsSource | null>(null);
  const [loadingSource, setLoadingSource] = useState(false);
  const [tested, setTested] = useState(false);
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<IAwsProfileValidationResult | null>(null);
  const [saving, setSaving] = useState(false);
  const serverMode = useServerMode();

  const whereLabel = serverMode === 'cloud' ? 'the server' : 'this machine';
  const set = (field: string, value: string) => setForm(prev => ({ ...prev, [field]: value }));
  const setTags = (tags: string[]) => setForm(prev => ({ ...prev, tags }));

  const importSource = async () => {
    setLoadingSource(true);
    try {
      const s = await api.get<IAwsCredentialsSource>('/api/resources/aws-profiles/available');
      setSource(s);
      return s;
    } catch {
      toast.error('Failed to scan for AWS credentials.');
      return null;
    } finally {
      setLoadingSource(false);
    }
  };

  const applyImportedProfile = (p: IAwsCredentialProfile) => {
    setMode('profile');
    setForm(prev => ({
      ...prev,
      profileName: p.name,
      defaultRegion: prev.defaultRegion || p.region || '',
    }));
    setStep(3);
  };

  const testConnection = async () => {
    setTesting(true);
    setResult(null);
    try {
      const r = await api.post<IAwsProfileValidationResult>(
        '/api/resources/aws-profiles/validate',
        mode === 'keys'
          ? { profileId: null, profile: {
              name: form.name || 'inline',
              accessKeyId: form.accessKeyId,
              secretAccessKey: form.secretAccessKey,
              sessionToken: form.sessionToken || undefined,
              defaultRegion: form.defaultRegion,
            } }
          : { profileId: null, profile: {
              name: form.name || 'inline',
              profileName: form.profileName,
              defaultRegion: form.defaultRegion,
            } },
      );
      setResult(r);
      setTested(true);
      if (r.ok) {
        toast.success(r.accountId ? `Connected — account ${r.accountId}` : 'Connection OK');
      }
    } catch {
      toast.error('Connection test failed.');
    } finally {
      setTesting(false);
    }
  };

  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name is required.'); return; }
    if (mode === 'profile' && !form.profileName?.trim()) { toast.error('Profile name is required.'); return; }
    if (mode === 'keys' && (!form.accessKeyId?.trim() || !form.secretAccessKey?.trim())) {
      toast.error('Access Key Id and Secret Access Key are required.');
      return;
    }
    setSaving(true);
    try {
      const payload: AwsProfileWizardInput = mode === 'profile'
        ? { ...form, accessKeyId: undefined, secretAccessKey: undefined, sessionToken: undefined }
        : { ...form, profileName: undefined };
      await onSave(payload);
    } catch {
      toast.error('Save failed.');
    } finally {
      setSaving(false);
    }
  };

  const guide = result ? guidedErrorFor(result) : null;
  const stepLabels = ['Credentials', 'Who is this?', 'Region & test', 'Save'];

  return (
    <div>
      <div style={{ display: 'flex', gap: 4, marginBottom: 12 }}>
        {[1, 2, 3, 4].map(i => (
          <div
            key={i}
            style={stepPillStyle(step === i, step > i)}
            onClick={() => i < step && setStep(i)}
            role="button"
            aria-label={`Step ${i}: ${stepLabels[i - 1]}`}
          >
            {step > i ? '✓ ' : ''}{i}. {stepLabels[i - 1]}
          </div>
        ))}
      </div>

      {step === 1 && (
        <div>
          <div style={{ fontSize: 13, color: '#F0F2F5' }}>Where do the AWS credentials live?</div>
          <div style={{ fontSize: 12, color: '#8EA0B8', marginTop: 4 }}>
            This only decides how you tell ShipRight which keys to use — builds fetch the ECR token with <code>aws</code> on {whereLabel}.
          </div>
          <div style={boxStyle}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <ZestButton onClick={importSource} disabled={loadingSource}
                zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'refresh' }}>
                {loadingSource ? 'Searching…' : source ? 'Scan again' : 'Find profiles on this machine'}
              </ZestButton>
            </div>
            {source && !source.fileExists && (
              <div style={{ marginTop: 10, fontSize: 12, color: '#E0A63C' }}>
                No AWS credentials file found on {whereLabel}.
                Run <code>aws configure</code> there, or paste explicit access keys instead.
              </div>
            )}
            {source && source.fileExists && (
              <div style={{ marginTop: 10 }}>
                <div style={{ fontSize: 12, color: '#8EA0B8', marginBottom: 6 }}>
                  Found {source.profiles.length} profile{source.profiles.length === 1 ? '' : 's'} in{' '}
                  ~/.aws/credentials{source.kind === 'Wsl' ? ' (inside WSL)' : ''}:
                </div>
                <select
                  defaultValue=""
                  onChange={e => {
                    const p = source.profiles.find(x => x.name === e.target.value);
                    if (p) applyImportedProfile(p);
                  }}
                  style={selectStyle}
                  aria-label="Import a profile"
                >
                  <option value="" disabled>Choose a profile to import…</option>
                  {source.profiles.map(p => (
                    <option key={p.name} value={p.name}>
                      {p.name}{p.region ? ` · ${p.region}` : ''}{!p.hasKeys ? ' · no keys' : ''}
                    </option>
                  ))}
                </select>
                {source.profiles.some(p => p.unsupportedReason) && (
                  <div style={{ marginTop: 8, fontSize: 11, color: '#E0A63C' }}>
                    One or more profiles use SSO / credential_process — those cannot be injected as plain env vars.
                  </div>
                )}
              </div>
            )}
            <InfoTip title="How to add credentials" id="import-tip">
              {`On ${whereLabel}, run "aws configure --profile <name>" and answer the prompts. That writes ~/.aws/credentials.
Alternatively, create an IAM access key in AWS and paste it in the next step instead.`}
            </InfoTip>
          </div>
          <div className="wizard-footer" style={{ display: 'flex', justifyContent: 'space-between', marginTop: 14 }}>
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
            <ZestButton onClick={() => setStep(2)} zest={{ buttonStyle: 'solid', semanticType: 'submit' }}>
              Skip — paste keys
            </ZestButton>
          </div>
        </div>
      )}

      {step === 2 && (
        <div>
          <div style={{ fontSize: 13, color: '#F0F2F5' }}>Who is this profile?</div>
          <div className="formRow">
            <label className="wizard-label">Credential Source</label>
            <select
              value={mode}
              onChange={e => setMode(e.target.value as 'profile' | 'keys')}
              style={selectStyle}
              aria-label="Credential source"
            >
              <option value="profile">Named profile (from ~/.aws/credentials)</option>
              <option value="keys">Explicit access keys</option>
            </select>
          </div>
          {mode === 'profile' ? (
            <div className="formRow">
              <label className="wizard-label">Profile Name</label>
              <ZestTextbox value={form.profileName ?? ''} onChange={e => set('profileName', e.target.value)}
                placeholder="e.g. shipright-prod" zest={{ stretch: true }} />
              <InfoTip title="How do I create a named profile?" id="profile-tip">
                {`Run "aws configure --profile shipright-prod" on ${whereLabel}, paste your access key id + secret, then come back and type that name here.`}
              </InfoTip>
            </div>
          ) : (
            <>
              <div className="formRow">
                <label className="wizard-label">Access Key Id</label>
                <ZestTextbox value={form.accessKeyId ?? ''} onChange={e => set('accessKeyId', e.target.value)}
                  placeholder="AKIA…" zest={{ stretch: true }} />
              </div>
              <div className="formRow">
                <label className="wizard-label">Secret Access Key</label>
                <ZestTextbox value={form.secretAccessKey ?? ''} onChange={e => set('secretAccessKey', e.target.value)}
                  placeholder="secret" type="password" zest={{ stretch: true }} />
              </div>
              <div className="formRow">
                <label className="wizard-label">Session Token</label>
                <ZestTextbox value={form.sessionToken ?? ''} onChange={e => set('sessionToken', e.target.value)}
                  placeholder="optional — for temporary credentials" type="password" zest={{ stretch: true }} />
              </div>
              <InfoTip title="Where do these come from?" id="keys-tip">
                Create an access key in AWS IAM (Users → your user → Security credentials). The secret is shown once — keep it somewhere safe. Keys are stored encrypted in ShipRight.
              </InfoTip>
            </>
          )}
          <div className="wizard-footer" style={{ display: 'flex', justifyContent: 'space-between', marginTop: 14 }}>
            <ZestButton onClick={() => setStep(1)} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Back</ZestButton>
            <ZestButton onClick={() => setStep(3)} zest={{ buttonStyle: 'solid', semanticType: 'submit' }}>Next</ZestButton>
          </div>
        </div>
      )}

      {step === 3 && (
        <div>
          <div style={{ fontSize: 13, color: '#F0F2F5' }}>Region &amp; verify</div>
          <div className="formRow">
            <label className="wizard-label">Default Region</label>
            <ZestTextbox value={form.defaultRegion ?? ''} onChange={e => set('defaultRegion', e.target.value)}
              placeholder="us-east-1" zest={{ stretch: true }} list="wizard-region-suggestions" />
            <InfoTip title="Which region?" id="region-tip">
              The region of your ECR registry (or your default — it only drives the API call; you can also set a region directly on each registry later).
            </InfoTip>
          </div>
          <div className="formRow">
            <ZestButton onClick={testConnection} disabled={testing}
              zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'refresh' }}>
              {testing ? 'Testing…' : tested ? 'Test again' : 'Test connection'}
            </ZestButton>
          </div>
          {guide && (
            <div style={{
              marginTop: 10, border: '1px solid rgba(224,102,102,0.5)', borderRadius: 6,
              padding: '8px 10px', fontSize: 12, background: 'rgba(224,102,102,0.08)',
            }}>
              <div style={{ color: '#E06060', fontWeight: 600 }}>{guide.title}</div>
              {guide.hint && <div style={{ color: '#C7D2E0', marginTop: 4 }}>{guide.hint}</div>}
              {result?.errorCode === 'aws-cli-missing' && <AwsCliInstallButton onInstalled={() => testConnection()} />}
            </div>
          )}
          {result?.ok && (
            <div style={{
              marginTop: 10, border: '1px solid rgba(76,201,140,0.5)', borderRadius: 6,
              padding: '8px 10px', fontSize: 12, background: 'rgba(76,201,140,0.08)', color: '#7CD9A8',
            }}>
              Connected ✓ {result.arn ? `(identity: ${result.arn})` : ''}
            </div>
          )}
          <div className="wizard-footer" style={{ display: 'flex', justifyContent: 'space-between', marginTop: 14 }}>
            <ZestButton onClick={() => setStep(2)} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Back</ZestButton>
            <ZestButton onClick={() => setStep(4)} zest={{ buttonStyle: 'solid', semanticType: 'submit' }}>Next</ZestButton>
          </div>
          <datalist id="wizard-region-suggestions">
            {commonAwsRegions.map(r => <option key={r} value={r} />)}
          </datalist>
        </div>
      )}

      {step === 4 && (
        <div>
          <div style={{ fontSize: 13, color: '#F0F2F5' }}>Name &amp; save</div>
          <div className="formRow">
            <label className="wizard-label">Name</label>
            <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
              placeholder="e.g. Production AWS" zest={{ stretch: true }} />
          </div>
          <div className="formRow">
            <label className="wizard-label">Tags</label>
            <TagsInput value={form.tags ?? []} onChange={setTags} allTags={allTags} id="wiz-tags" />
          </div>
          <div className="wizard-footer" style={{ display: 'flex', justifyContent: 'space-between', marginTop: 14 }}>
            <ZestButton onClick={() => setStep(3)} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Back</ZestButton>
            <ZestButton onClick={handleSubmit} disabled={saving}
              zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
              {saving ? 'Saving…' : isEdit ? 'Update AWS Profile' : 'Create AWS Profile'}
            </ZestButton>
          </div>
        </div>
      )}
    </div>
  );
}