import { screen } from '@testing-library/react';
import { ApiError } from '../../lib/errors';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, makeCase, makeUser } from '../../test/fakes';

describe('NegotiationPage', () => {
  it('offers the legal transition for the current stage and hides the others', async () => {
    const api = new FakeApi();
    api.cases = [makeCase({ id: 'case-1', state: 'Open' })];

    const { user } = renderApp({ api, route: '/negotiation/case-1' });

    const start = await screen.findByRole('button', { name: 'Iniciar negociación' });
    expect(screen.queryByRole('button', { name: 'Cerrar caso' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Marcar como acordado' })).not.toBeInTheDocument();

    await user.click(start);

    expect(await screen.findByRole('button', { name: 'Pasar a pendiente' })).toBeInTheDocument();
    expect(api.calls).toContain('transitionCase');
  });

  it('hides the transition controls without negotiation.write', async () => {
    const api = new FakeApi();
    api.me = { ...api.me, capabilities: ['negotiation.read'] };
    api.cases = [makeCase({ id: 'case-1', state: 'Open' })];

    renderApp({ api, route: '/negotiation/case-1' });

    expect(await screen.findByText(/Caso iniciado/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Iniciar negociación' })).not.toBeInTheDocument();
  });

  it('only offers promotion on a closed case', async () => {
    const api = new FakeApi();
    api.cases = [makeCase({ id: 'case-1', state: 'Closed', closedAt: '2026-02-01T10:00:00Z' })];

    renderApp({ api, route: '/negotiation/case-1' });

    expect(await screen.findByRole('button', { name: 'Promover a convención' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reabrir' })).toBeInTheDocument();
  });

  it('hides promotion without negotiation.promote', async () => {
    const api = new FakeApi();
    api.me = { ...api.me, capabilities: ['negotiation.read', 'negotiation.write'] };
    api.cases = [makeCase({ id: 'case-1', state: 'Closed', closedAt: '2026-02-01T10:00:00Z' })];

    renderApp({ api, route: '/negotiation/case-1' });

    await screen.findByText(/Caso iniciado/);
    expect(screen.queryByRole('button', { name: 'Promover a convención' })).not.toBeInTheDocument();
  });
});

describe('UsersPage', () => {
  it('blocks and unblocks a user through the API', async () => {
    const api = new FakeApi();
    api.users = [makeUser({ id: 'user-2', displayName: 'Persona Bloqueable', isBlocked: false })];

    const { user } = renderApp({ api, route: '/admin/users' });

    await user.click(await screen.findByRole('button', { name: 'Bloquear' }));

    expect(await screen.findByText(/su token deja de valer/i)).toBeInTheDocument();
    expect(api.calls).toContain('updateUser');
    expect(screen.getByRole('button', { name: 'Desbloquear' })).toBeInTheDocument();
  });

  it('surfaces the API refusal instead of inventing access', async () => {
    const api = new FakeApi();
    api.me = { ...api.me, capabilities: ['document.read'] };
    api.errors.listUsers = new ApiError(403, 'forbidden', 'La capacidad user.manage es necesaria.');

    renderApp({ api, route: '/admin/users' });

    expect(await screen.findByText(/user.manage/)).toBeInTheDocument();
  });
});
