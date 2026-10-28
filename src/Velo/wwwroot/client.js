// Thin fetch wrapper for the /api endpoints. Non-GET requests carry the X-Velo header.
export async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (method !== 'GET') opts.headers['X-Velo'] = '1';
  if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
  const res = await fetch('/api' + path, opts);
  if (!res.ok) {
    let msg = res.status + ' ' + res.statusText;
    try { msg = (await res.json()).error || msg; } catch { /* not json */ }
    const err = new Error(msg); err.status = res.status; throw err;
  }
  if (res.status === 204) return null;
  return (res.headers.get('content-type') || '').includes('json') ? res.json() : res.text();
}
