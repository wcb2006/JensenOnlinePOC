import { API_BASE } from './config.js';

// Säker tokenhantering:
// - Access token (kortlivad JWT) finns ENDAST i minnet – aldrig i localStorage/sessionStorage där XSS kan läsa den.
// - Refresh token ligger i en HttpOnly-cookie som JavaScript inte kan komma åt.
// - Vid omladdning av sidan hämtas en ny access token via /api/auth/refresh.
let accessToken = null;
let currentUser = null;
const listeners = new Set();

export const session = {
  get user() { return currentUser; },
  get isLoggedIn() { return currentUser !== null; },
  get isAdmin() { return currentUser?.role === 'Admin'; },
  onChange(fn) { listeners.add(fn); },
};

function setSession(auth) {
  accessToken = auth?.accessToken ?? null;
  currentUser = auth?.user ?? null;
  listeners.forEach((fn) => fn(currentUser));
}

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail || problem?.title || `Något gick fel (${status}).`);
    this.status = status;
    this.title = problem?.title;
    this.fieldErrors = problem?.errors ?? {};
    this.traceId = problem?.traceId;
  }
}

async function request(path, { method = 'GET', body, retry = true } = {}) {
  const isAuthEndpoint = path.startsWith('/api/auth/');
  // Egen header krävs av API:t för cookie-endpoints (CSRF-skydd) – kan inte skickas från andra origins utan CORS-godkännande.
  const headers = { 'X-Requested-With': 'JensenOnline' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (accessToken) headers['Authorization'] = `Bearer ${accessToken}`;

  let response;
  try {
    response = await fetch(API_BASE + path, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      // Cookies (refresh token) skickas bara till auth-endpoints.
      credentials: isAuthEndpoint ? 'include' : 'omit',
      cache: 'no-store',
    });
  } catch {
    throw new ApiError(0, { title: 'Kunde inte nå servern.', detail: 'Kontrollera att API:t körs på ' + API_BASE });
  }

  // Access token har gått ut => försök förnya en gång och gör om anropet.
  if (response.status === 401 && retry && accessToken && !isAuthEndpoint) {
    if (await refreshSession()) return request(path, { method, body, retry: false });
  }

  if (response.status === 204) return null;
  const isJson = (response.headers.get('content-type') || '').includes('json');
  const data = isJson ? await response.json() : null;
  if (!response.ok) throw new ApiError(response.status, data);
  return data;
}

let refreshInFlight = null;
export function refreshSession() {
  refreshInFlight ??= (async () => {
    try {
      setSession(await request('/api/auth/refresh', { method: 'POST', retry: false }));
      return true;
    } catch {
      setSession(null);
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

export const api = {
  register: (email, password) => request('/api/auth/register', { method: 'POST', body: { email, password } }),
  login: async (email, password) => {
    setSession(await request('/api/auth/login', { method: 'POST', body: { email, password } }));
    return currentUser;
  },
  logout: async () => {
    try { await request('/api/auth/logout', { method: 'POST' }); } finally { setSession(null); }
  },

  products: (search = '') => request('/api/products' + (search ? `?search=${encodeURIComponent(search)}` : '')),
  createProduct: (product) => request('/api/products', { method: 'POST', body: product }),
  updateProduct: (id, product) => request(`/api/products/${encodeURIComponent(id)}`, { method: 'PUT', body: product }),
  deleteProduct: (id) => request(`/api/products/${encodeURIComponent(id)}`, { method: 'DELETE' }),

  createOrder: (items) => request('/api/orders', { method: 'POST', body: { items } }),
  myOrders: () => request('/api/orders/mine'),

  adminOrders: () => request('/api/admin/orders'),
  setOrderStatus: (id, status) => request(`/api/admin/orders/${encodeURIComponent(id)}/status`, { method: 'PUT', body: { status } }),
  adminUsers: () => request('/api/admin/users'),
  setUserRole: (id, role) => request(`/api/admin/users/${encodeURIComponent(id)}/role`, { method: 'PUT', body: { role } }),
  setUserDisabled: (id, isDisabled) => request(`/api/admin/users/${encodeURIComponent(id)}/status`, { method: 'PUT', body: { isDisabled } }),
  auditLogs: () => request('/api/admin/audit-logs?take=200'),
};
