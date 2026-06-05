import { useState } from 'react';
import { Tab, TabList, TabPanel, Tabs } from 'react-tabs';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { RiAddLine, RiDeleteBinLine } from 'react-icons/ri';
import { IApiError, IProjectInput, emptyProjectInput } from '@/shared/types/IProject';
import styles from './Styles/ProjectConfigForm.module.css';

interface Props {
  initial?: IProjectInput;
  onSave: (data: IProjectInput) => Promise<void>;
  onCancel: () => void;
}

export default function ProjectConfigForm({ initial, onSave, onCancel }: Props) {
  const [form, setForm] = useState<IProjectInput>(initial ?? emptyProjectInput());
  const [errors, setErrors] = useState<Record<string, string>>({});

  const set = (path: string, value: string) => {
    const parts = path.split('.');
    setForm(prev => {
      const next = structuredClone(prev) as Record<string, unknown>;
      let obj = next;
      for (let i = 0; i < parts.length - 1; i++) obj = obj[parts[i]] as Record<string, unknown>;
      obj[parts[parts.length - 1]] = value;
      return next as IProjectInput;
    });
    setErrors(prev => { const e = { ...prev }; delete e[path]; return e; });
  };

  const setService = (i: number, field: string, value: string) => {
    setForm(prev => ({
      ...prev,
      services: prev.services.map((s, idx) => idx === i ? { ...s, [field]: value } : s),
    }));
    setErrors(prev => { const e = { ...prev }; delete e[`services[${i}].${field}`]; return e; });
  };

  const addService = () => setForm(prev => ({
    ...prev,
    services: [...prev.services, { name: '', versionFilePath: '', buildContextPath: '', dockerImageName: '' }],
  }));

  const removeService = (i: number) => setForm(prev => ({
    ...prev, services: prev.services.filter((_, idx) => idx !== i),
  }));

  const handleSave = async () => {
    setErrors({});
    try {
      await onSave(form);
    } catch (errs: unknown) {
      const apiErrors: Record<string, string> = {};
      const list = Array.isArray(errs) ? errs : [errs];
      (list as IApiError[]).forEach(e => { if (e.field) apiErrors[e.field] = e.message; });
      setErrors(apiErrors);
      throw errs;
    }
  };

  const tabHasError = (keys: string[]) =>
    keys.some(k => Object.keys(errors).some(e => e.startsWith(k)));

  const tabClass = (keys: string[]) =>
    [styles.tab, tabHasError(keys) ? styles.tabError : ''].join(' ');

  return (
    <div>
      <Tabs>
        <TabList className={styles.tabList}>
          {[
            { label: 'General',   keys: ['name'] },
            { label: 'Services',  keys: ['services'] },
            { label: 'Git & WSL', keys: ['git', 'wsl'] },
            { label: 'Server',    keys: ['server'] },
          ].map(({ label, keys }) => (
            <Tab key={label} className={tabClass(keys)} selectedClassName={styles.tabActive}>
              {label}
              {tabHasError(keys) && <span className={styles.errorDot}>●</span>}
            </Tab>
          ))}
        </TabList>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          <Field label="Project Name" error={errors['name']}>
            <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
              placeholder="e.g. SMS Gateway" maxLength={100} zest={{ stretch: true, zSize: 'md' }} />
          </Field>
        </TabPanel>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          {form.services.map((svc, i) => (
            <div key={i} className={styles.serviceCard}>
              <div className={styles.serviceCardHeader}>
                <span className={styles.serviceLabel}>Service {i + 1}</span>
                {form.services.length > 1 && (
                  <ZestButton zest={{ visualOptions: { variant: 'danger', size: 'sm' } }} onClick={() => removeService(i)}>
                    <RiDeleteBinLine />
                  </ZestButton>
                )}
              </div>
              <Field label="Service Name" error={errors[`services[${i}].name`]}>
                <ZestTextbox value={svc.name} onChange={e => setService(i, 'name', e.target.value)}
                  placeholder="e.g. API" maxLength={100} zest={{ stretch: true }} />
              </Field>
              <Field label="Version File Path" error={errors[`services[${i}].versionFilePath`]}>
                <ZestTextbox value={svc.versionFilePath} onChange={e => setService(i, 'versionFilePath', e.target.value)}
                  placeholder="/mnt/d/work/.../version.txt" zest={{ stretch: true }} />
              </Field>
              <Field label="Build Context Path" error={errors[`services[${i}].buildContextPath`]}>
                <ZestTextbox value={svc.buildContextPath} onChange={e => setService(i, 'buildContextPath', e.target.value)}
                  placeholder="/mnt/d/work/..." zest={{ stretch: true }} />
              </Field>
              <Field label="Docker Image Name" error={errors[`services[${i}].dockerImageName`]}>
                <ZestTextbox value={svc.dockerImageName} onChange={e => setService(i, 'dockerImageName', e.target.value)}
                  placeholder="nyingi/jattac-sms" zest={{ stretch: true }} />
              </Field>
            </div>
          ))}
          {form.services.length < 10 && (
            <ZestButton onClick={addService} zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}>
              <RiAddLine /> Add Service
            </ZestButton>
          )}
        </TabPanel>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          <Field label="Git Repository Path" error={errors['git.repoPath']}>
            <ZestTextbox value={form.git.repoPath} onChange={e => set('git.repoPath', e.target.value)}
              placeholder="/mnt/d/work/nyingi/code/systems/sms-gateway" zest={{ stretch: true }} />
          </Field>
          <Field label="Deploy Branch" error={errors['git.deployBranch']}>
            <ZestTextbox value={form.git.deployBranch} onChange={e => set('git.deployBranch', e.target.value)}
              placeholder="master" maxLength={100} zest={{ stretch: true }} />
          </Field>
          <Field label="WSL Working Directory" error={errors['wsl.workingDir']}>
            <ZestTextbox value={form.wsl.workingDir} onChange={e => set('wsl.workingDir', e.target.value)}
              placeholder="/home/nyingi/work/jattac/docker/..." zest={{ stretch: true }} />
          </Field>
        </TabPanel>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          <Field label="Host" error={errors['server.host']}>
            <ZestTextbox value={form.server.host} onChange={e => set('server.host', e.target.value)}
              placeholder="3.130.65.46" zest={{ stretch: true }} />
          </Field>
          <Field label="Username" error={errors['server.username']}>
            <ZestTextbox value={form.server.username} onChange={e => set('server.username', e.target.value)}
              placeholder="ubuntu" zest={{ stretch: true }} />
          </Field>
          <Field label="SSH Key Path" error={errors['server.sshKeyPath']}>
            <ZestTextbox value={form.server.sshKeyPath} onChange={e => set('server.sshKeyPath', e.target.value)}
              placeholder="/home/nyingi/.../key.pem" zest={{ stretch: true }} />
          </Field>
          <Field label="Remote Working Directory" error={errors['server.remoteWorkingDir']}>
            <ZestTextbox value={form.server.remoteWorkingDir} onChange={e => set('server.remoteWorkingDir', e.target.value)}
              placeholder="/home/ubuntu/jattac-sms-gateway-docker" zest={{ stretch: true }} />
          </Field>
          <Field label="Rebuild Script" error={errors['server.rebuildScript']}>
            <ZestTextbox value={form.server.rebuildScript} onChange={e => set('server.rebuildScript', e.target.value)}
              placeholder="rebuild.sh" maxLength={100} zest={{ stretch: true }} />
          </Field>
        </TabPanel>
      </Tabs>

      <div className={styles.footer}>
        <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          Save Project
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}

function Field({ label, error, children }: { label: string; error?: string; children: React.ReactNode }) {
  return (
    <div className={styles.formRow}>
      <label className={styles.label}>{label}</label>
      {children}
      {error && <p className={styles.errorText}>{error}</p>}
    </div>
  );
}
