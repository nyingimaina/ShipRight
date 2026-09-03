import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import styles from '../Styles/ProjectSetupWizard.module.css';
import { ServiceDraft, emptyServiceDraft } from './types';

interface Props {
  services: ServiceDraft[];
  composeNames: string[];
  errors: Record<string, string>;
  onServicesChange: (services: ServiceDraft[]) => void;
}

export default function ServicesAtom({ services, composeNames, errors, onServicesChange }: Props) {
  const addService = () => {
    if (services.length < 10) onServicesChange([...services, emptyServiceDraft()]);
  };
  const removeService = (i: number) => onServicesChange(services.filter((_, idx) => idx !== i));
  const updateService = (i: number, patch: Partial<ServiceDraft>) =>
    onServicesChange(services.map((s, j) => j === i ? { ...s, ...patch } : s));

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
