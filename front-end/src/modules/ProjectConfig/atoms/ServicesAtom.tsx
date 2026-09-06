import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import { IDockerRegistryResource } from '@/shared/types/IProject';
import styles from '../Styles/ProjectSetupWizard.module.css';
import { ServiceDraft, emptyServiceDraft } from './types';

interface Props {
  services: ServiceDraft[];
  composeNames: string[];
  registries: IDockerRegistryResource[];
  errors: Record<string, string>;
  onServicesChange: (services: ServiceDraft[]) => void;
  onRegistriesChange: (registries: IDockerRegistryResource[]) => void;
}

const registryTags = (registries: IDockerRegistryResource[]) =>
  Array.from(new Set(registries.flatMap(r => r.tags ?? []))).sort();

export default function ServicesAtom({
  services, composeNames, registries, errors, onServicesChange, onRegistriesChange,
}: Props) {
  const [creatingFor, setCreatingFor] = useState<number | null>(null);
  const [newName, setNewName] = useState('');
  const [newRegistry, setNewRegistry] = useState('');
  const [filterTag, setFilterTag] = useState<string>('');

  const addService = () => {
    if (services.length < 10) onServicesChange([...services, emptyServiceDraft()]);
  };
  const removeService = (i: number) => onServicesChange(services.filter((_, idx) => idx !== i));
  const updateService = (i: number, patch: Partial<ServiceDraft>) =>
    onServicesChange(services.map((s, j) => j === i ? { ...s, ...patch } : s));

  const allTags = registryTags(registries);
  const visibleRegistries = filterTag
    ? registries.filter(r => (r.tags ?? []).includes(filterTag))
    : registries;

  const createRegistry = async (serviceIndex: number) => {
    if (!newName.trim() || !newRegistry.trim()) return;
    try {
      const created = await api.post<IDockerRegistryResource>('/api/resources/registries', {
        name: newName.trim(), registry: newRegistry.trim(),
      });
      onRegistriesChange([...registries, created]);
      updateService(serviceIndex, { dockerRegistryResourceId: created.id, dockerRegistry: created.registry });
      setCreatingFor(null);
      setNewName('');
      setNewRegistry('');
    } catch {
      toast.error('Failed to create registry resource');
    }
  };

  const selectRegistry = (i: number, registryId: string) => {
    const reg = registries.find(r => r.id === registryId);
    updateService(i, reg
      ? { dockerRegistryResourceId: registryId, dockerRegistry: reg.registry }
      : { dockerRegistryResourceId: undefined });
  };

  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Services</span>
      {services.length === 0 && (
        <div className={styles.warningBox}>
          No services detected. Ensure each service has a version.txt alongside a Dockerfile.
        </div>
      )}
      {errors['services'] && <p className={styles.errorText}>{errors['services']}</p>}
      {services.map((svc, i) => (
        <div key={i} className={styles.serviceCard}>
          <div className={styles.serviceHeader}>
            <span className={styles.serviceName}>{svc.name || `Service ${i + 1}`}</span>
            {svc.version && <span className={styles.versionChip}>v{svc.version}</span>}
            {svc.dockerImageName
              ? <span className={styles.detectedBadge}>image detected</span>
              : <span className={styles.needsBadge}>needs image name</span>}
            <ZestButton onClick={() => removeService(i)} zest={{ visualOptions: { variant: 'danger', size: 'sm' } }}>Remove</ZestButton>
          </div>

          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Service name</span>
            <ZestTextbox value={svc.name}
              onChange={e => updateService(i, { name: e.target.value })}
              zest={{ stretch: true, zSize: 'sm' }}
              list="svc-name-suggestions" />
            <datalist id="svc-name-suggestions">
              {composeNames.map(n => <option key={n} value={n} />)}
            </datalist>
            {errors[`services[${i}].name`] && <p className={styles.errorText}>{errors[`services[${i}].name`]}</p>}
          </div>

          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Docker image name <span style={{ color: '#C9A84C' }}>*</span></span>
            <ZestTextbox value={svc.dockerImageName ?? ''}
              onChange={e => updateService(i, { dockerImageName: e.target.value })}
              placeholder="e.g. nyingi/jattac-sms"
              zest={{ stretch: true, zSize: 'sm' }} />
            {errors[`services[${i}].dockerImageName`] && (
              <p className={styles.errorText}>{errors[`services[${i}].dockerImageName`]}</p>
            )}
          </div>

          {registries.length > 0 && (
            <div className={styles.fieldRow}>
              <span className={styles.fieldLabel}>Saved registry resource</span>
              {creatingFor === i ? (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 6, width: '100%' }}>
                  <ZestTextbox value={newName} onChange={e => setNewName(e.target.value)}
                    placeholder="Registry resource name (e.g. company-ecr)" zest={{ stretch: true, zSize: 'sm' }} />
                  <ZestTextbox value={newRegistry} onChange={e => setNewRegistry(e.target.value)}
                    placeholder="e.g. 123456789012.dkr.ecr.us-east-1.amazonaws.com" zest={{ stretch: true, zSize: 'sm' }} />
                  <div style={{ display: 'flex', gap: 6 }}>
                    <ZestButton onClick={() => createRegistry(i)}
                      zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>Save</ZestButton>
                    <ZestButton onClick={() => { setCreatingFor(null); setNewName(''); setNewRegistry(''); }}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Cancel</ZestButton>
                  </div>
                </div>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 4, width: '100%' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                    <select value={svc.dockerRegistryResourceId ?? ''}
                      onChange={e => selectRegistry(i, e.target.value)}
                      style={{ flex: 1, background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', fontSize: 13 }}>
                      <option value="">— None (type registry host below) —</option>
                      {visibleRegistries.map(r => (
                        <option key={r.id} value={r.id}>{r.name}{r.authType === 'AwsEcr' ? ' (AWS ECR)' : ''}</option>
                      ))}
                    </select>
                    <ZestButton onClick={() => setCreatingFor(i)}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>+ New</ZestButton>
                  </div>
                  {allTags.length > 0 && (
                    <select value={filterTag} onChange={e => setFilterTag(e.target.value)}
                      style={{ background: '#131D30', color: '#A8B8CC', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '3px 8px', fontSize: 12 }}>
                      <option value="">All registries</option>
                      {allTags.map(t => <option key={t} value={t}>{t}</option>)}
                    </select>
                  )}
                </div>
              )}
            </div>
          )}

          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Docker registry</span>
            <ZestTextbox value={svc.dockerRegistry ?? ''}
              onChange={e => updateService(i, { dockerRegistry: e.target.value })}
              placeholder="e.g. ghcr.io (leave empty for Docker Hub)"
              zest={{ stretch: true, zSize: 'sm' }} />
            <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
              Optional — only needed for non-Docker Hub registries.
            </p>
          </div>

          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>
              Compose service name
              {svc.composeServiceName && <span className={styles.detectedBadge} style={{ marginLeft: 6 }}>auto-detected</span>}
            </span>
            <ZestTextbox value={svc.composeServiceName ?? ''}
              onChange={e => updateService(i, { composeServiceName: e.target.value })}
              placeholder="e.g. api (key in docker-compose.yml)"
              zest={{ stretch: true, zSize: 'sm' }}
              list="compose-name-suggestions" />
            <datalist id="compose-name-suggestions">
              {composeNames.map(n => <option key={n} value={n} />)}
            </datalist>
            <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
              Optional — when set on all services, only those containers restart (nginx/minio stay up).
            </p>
          </div>

          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Docker username</span>
            <ZestTextbox value={svc.dockerUsername ?? ''}
              onChange={e => updateService(i, { dockerUsername: e.target.value })}
              placeholder="registry username" zest={{ stretch: true, zSize: 'sm' }} />
            <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
              Optional — saved credentials skip the login prompt during push.
            </p>
          </div>
          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Docker password / token</span>
            <input type="password" value={svc.dockerPassword ?? ''}
              onChange={e => updateService(i, { dockerPassword: e.target.value })}
              placeholder="Enter to set or update"
              autoComplete="new-password"
              style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)',
                borderRadius: 6, padding: '6px 10px', fontSize: 14, boxSizing: 'border-box' }} />
            <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
              Encrypted at rest with AES-256-GCM. Leave blank to keep existing or be prompted at build time.
            </p>
          </div>

          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Version file</span>
            <div className={styles.fieldValue}>{svc.versionFilePath}</div>
          </div>
          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Build context</span>
            <div className={styles.fieldValue}>{svc.buildContextPath}</div>
          </div>
        </div>
      ))}
      {services.length < 10 && (
        <ZestButton onClick={addService} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>+ Add Service</ZestButton>
      )}
    </div>
  );
}