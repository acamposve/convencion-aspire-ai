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

  // #135: /comparison/headings answers with groups that nest their headings. Sending a group id where a
  // heading id is expected is what broke the comparison, the clause editor and the statute page, so the
  // flattening is asserted here, at the boundary, and not only through a screen.
  it('flattens heading groups into a deterministic heading list', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(200, [
        {
          id: 'g1',
          name: 'GENERALES',
          description: null,
          requiresEconomicComparison: false,
          headings: [
            {
              id: 'h-vac',
              groupId: 'g1',
              name: 'Vacaciones anuales',
              description: null,
              isExcluded: false,
              sortOrder: 20,
            },
            {
              id: 'h-nc',
              groupId: 'g1',
              name: 'No Compara',
              description: null,
              isExcluded: true,
              sortOrder: 0,
            },
          ],
        },
        {
          id: 'g2',
          name: 'ECONOMICO',
          description: null,
          requiresEconomicComparison: true,
          headings: [
            {
              id: 'h-sal',
              groupId: 'g2',
              name: 'Estructura salarial',
              description: null,
              isExcluded: false,
              sortOrder: 10,
            },
          ],
        },
        // A group without headings must be skipped, not throw.
        { id: 'g3', name: 'SIN MATERIAS', description: null, requiresEconomicComparison: false },
      ]),
    );

    const api = new HttpApiClient('http://api.test', authWith('token-123'));
    const headings = await api.listHeadings();

    expect(headings.map((heading) => heading.id)).toEqual(['h-nc', 'h-vac', 'h-sal']);
    expect(headings[0]).toMatchObject({
      groupId: 'g1',
      groupName: 'GENERALES',
      isExcluded: true,
      requiresEconomicComparison: false,
    });
    expect(headings[2]).toMatchObject({
      groupName: 'ECONOMICO',
      requiresEconomicComparison: true,
    });

    const call = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(call[0]).toBe('http://api.test/comparison/headings?includeExcluded=true');
  });
});
