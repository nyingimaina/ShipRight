import { sseUrl } from '@/shared/ApiService';

export interface ExportOptions {
  includeSecrets?: boolean;
  passphrase?: string;
}

/** Downloads the project's export bundle through the browser's normal save flow.
 *  Secrets are opt-in and always passphrase-protected; the acknowledgement is checked by the UI. */
export async function exportProject(projectId: string, options: ExportOptions = {}): Promise<void> {
  const includeSecrets = options.includeSecrets === true;
  const query = includeSecrets ? '?includeSecrets=true' : '';
  const headers: Record<string, string> = {};
  if (includeSecrets) {
    headers['X-ShipRight-Secrets-Acknowledged'] = 'true';
    headers['X-ShipRight-Passphrase'] = options.passphrase ?? '';
  }

  const res = await fetch(sseUrl(`/api/projects/${encodeURIComponent(projectId)}/export${query}`), {
    method: 'POST',
    credentials: 'include',
    headers,
  });
  if (!res.ok) {
    const data = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
    throw new Error(data.message ?? `HTTP ${res.status}`);
  }

  const disposition = res.headers.get('content-disposition') ?? '';
  const fallback = includeSecrets
    ? `${projectId}-shipright-secret-export.shrsec`
    : `${projectId}-shipright-export.zip`;
  const filename = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1] ?? fallback;

  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
