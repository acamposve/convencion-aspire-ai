import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, FakeAuth, FakePublicApi } from '../../test/fakes';

describe('PortalPage', () => {
  it('renders published content with no session and no token', async () => {
    const auth = new FakeAuth({ token: null });
    const api = new FakeApi();
    const publicApi = new FakePublicApi();

    publicApi.agreements = [
      {
        id: 'a1',
        title: 'Empresa Uno · 2026',
        companyName: 'Empresa Uno',
        validFrom: '2026-01-01',
        validTo: null,
        scope: 'Nacional',
        publishedAt: '2026-02-01T10:00:00Z',
      },
    ];
    publicApi.agreementDetails.set('a1', {
      ...publicApi.agreements[0]!,
      clauses: [
        {
          number: 1,
          title: 'Salario básico',
          summary: null,
          body: '<p>Texto público.</p>',
        },
      ],
      annexes: [],
    });
    publicApi.news = [
      {
        id: 'n1',
        title: 'Nueva convención',
        summary: 'Resumen público',
        categoryName: 'Noticias',
        publishedAt: '2026-02-02T10:00:00Z',
      },
    ];

    const { user } = renderApp({ auth, api, publicApi, route: '/portal' });

    expect(await screen.findByText('Empresa Uno · 2026')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Cerrar sesión/ })).not.toBeInTheDocument();
    expect(auth.token).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Leer' }));

    expect(await screen.findByText(/Salario básico/)).toBeInTheDocument();
    expect(screen.getByText('Texto público.')).toBeInTheDocument();

    // The authenticated client was never used: the portal has no session to use.
    expect(api.calls).not.toContain('getMe');
    expect(publicApi.calls).toBeGreaterThan(0);
  });

  it('shows the news tab from the public client', async () => {
    const publicApi = new FakePublicApi();
    publicApi.news = [
      {
        id: 'n1',
        title: 'Nueva convención',
        summary: null,
        categoryName: 'Noticias',
        publishedAt: '2026-02-02T10:00:00Z',
      },
    ];

    const { user } = renderApp({
      auth: new FakeAuth({ token: null }),
      publicApi,
      route: '/portal',
    });

    await user.click(await screen.findByRole('tab', { name: 'Noticias' }));

    expect(await screen.findByText('Nueva convención')).toBeInTheDocument();
  });
});
