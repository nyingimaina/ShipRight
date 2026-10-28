import { sseUrl } from '@/shared/ApiService';

/** Downloads the project's export bundle (a zip) through the browser's normal save flow. */
export async function exportProject(projectId: string): Promise<void> {
  const res = await fetch(sseUrl(`/api/projects/${encodeURIComponent(projectId)}/export`), {
    method: 'POST',
    credentials: 'include',
  });
  if (!res.ok) {
    const data = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
    throw new Error(data.message ?? `HTTP ${res.status}`);
  }

  const disposition = res.headers.get('content-disposition') ?? '';
  const filename = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1] ?? `${projectId}-shipright-export.zip`;

  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
