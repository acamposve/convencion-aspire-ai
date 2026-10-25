import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HttpApiClient } from './api';
import type { AuthClient } from './auth';

function authWith(token: string | null): AuthClient {
  return {
    isConfigured: true,
    signIn: async () => undefined,
    signOut: async () => undefined,
    getAccessToken: async () => token,
  };
}

function jsonResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

describe('HttpApiClient', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends the bearer token and parses the profile', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(200, {
        userId: 'u1',
        email: 'ana@example.test',
        displayName: 'Ana',
        companyId: 'c1',
        capabilities: ['document.read'],
      }),
    );

    const api = new HttpApiClient('http://api.test/', authWith('token-123'));
    const me = await api.getMe();

    expect(me.email).toBe('ana@example.test');

    const call = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(call[0]).toBe('http://api.test/me');
    expect((call[1].headers as Headers).get('Authorization')).toBe('Bearer token-123');
    expect((call[1].headers as Headers).get('Accept')).toBe('application/json');
  });

  it('maps a problem+json failure to an ApiError with its code and status', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(403, {
        code: 'forbidden',
        detail: 'The capability is required.',
        correlationId: 'corr-1',
      }),
    );

    const api = new HttpApiClient('http://api.test', authWith('token-123'));

    await expect(api.getMe()).rejects.toMatchObject({
      name: 'ApiError',
      status: 403,
      code: 'forbidden',
      detail: 'The capability is required.',
      correlationId: 'corr-1',
    });
  });

  it('refuses to call the API without a token', async () => {
    const api = new HttpApiClient('http://api.test', authWith(null));

    await expect(api.getMe()).rejects.toMatchObject({
      status: 401,
      code: 'unauthenticated',
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('encodes the list pagination in the query string', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(200, { items: [], page: 2, pageSize: 5, total: 0 }),
    );

    const api = new HttpApiClient('http://api.test', authWith('token-123'));
    await api.listDocuments(2, 5);

    const call = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(call[0]).toBe('http://api.test/documents?page=2&pageSize=5');
  });
});
