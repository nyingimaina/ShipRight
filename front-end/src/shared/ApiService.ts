const BASE = process.env.NEXT_PUBLIC_API_URL ?? '';

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
  });

  if (res.status === 204) return undefined as T;

  const data = await res.json().catch(() => ({ isError: true, message: `HTTP ${res.status}` }));
  if (!res.ok) throw data;
  return data as T;
}

export const api = {
  get:    <T>(path: string)               => request<T>('GET',    path),
  post:   <T>(path: string, body: unknown) => request<T>('POST',   path, body),
  put:    <T>(path: string, body: unknown) => request<T>('PUT',    path, body),
  delete: <T>(path: string)               => request<T>('DELETE', path),
};
