import { API_BASE } from '../config';

export interface ApiResult {
  ok: boolean;
  error?: string;
}

/** POST a JSON body to the site API; network failures become friendly errors. */
export async function postJson(path: string, body: unknown): Promise<ApiResult> {
  try {
    const res = await fetch(`${API_BASE}${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = (await res.json().catch(() => null)) as ApiResult | null;
    if (data) return data;
    return { ok: res.ok, error: res.ok ? undefined : 'Unexpected response. Please try again.' };
  } catch {
    return { ok: false, error: 'Network error. Check your connection and try again.' };
  }
}
