import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiError, AuthError } from '../../lib/errors';
import { FakeApi, FakeAuth } from '../../test/fakes';
import { renderApp } from '../../test/renderApp';

describe('LoginPage', () => {
  it('signs in, reaches the documents list and stores no token in the browser', async () => {
    const auth = new FakeAuth();
    const api = new FakeApi();
    const { user } = renderApp({ auth, api, route: '/login' });
    const typedValue = 'clave-segura';

    await user.type(await screen.findByLabelText(/Correo electrónico/), 'ana@example.test');
    await user.type(screen.getByLabelText(/Contraseña/), typedValue);
    await user.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();
    expect(await screen.findByText('Empresa Demo')).toBeInTheDocument();
    expect(auth.signedInWith).toEqual({ email: 'ana@example.test', password: typedValue });

    // The access token lives in memory only: nothing may be written to web storage.
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('shows the authentication error code, not a raw provider message', async () => {
    const auth = new FakeAuth({
      signInError: new AuthError('invalid_credentials', 'El proveedor dijo algo interno.'),
    });
    const { user } = renderApp({ auth, api: new FakeApi(), route: '/login' });

    await user.type(await screen.findByLabelText(/Correo electrónico/), 'ana@example.test');
    await user.type(screen.getByLabelText(/Contraseña/), 'incorrecta');
    await user.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByText('invalid_credentials')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Documentos' })).not.toBeInTheDocument();
    expect(screen.queryByText('El proveedor dijo algo interno.')).not.toBeInTheDocument();
  });

  it('shows the API code when the identity is valid but no profile exists', async () => {
    const api = new FakeApi();
    api.meError = new ApiError(403, 'no_profile', 'No account exists for this identity.');
    const { user } = renderApp({ auth: new FakeAuth(), api, route: '/login' });

    await user.type(await screen.findByLabelText(/Correo electrónico/), 'ana@example.test');
    await user.type(screen.getByLabelText(/Contraseña/), 'clave-segura');
    await user.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByRole('heading', { name: 'Acceso no autorizado' })).toBeInTheDocument();
    expect(screen.getByText('no_profile')).toBeInTheDocument();
  });

  it('fails closed when Supabase is not configured', async () => {
    renderApp({
      auth: new FakeAuth({ configured: false }),
      api: new FakeApi(),
      route: '/login',
    });

    expect(await screen.findByText('auth_not_configured')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Entrar' })).not.toBeInTheDocument();
  });

  it('accepts a development token without writing it to storage', async () => {
    const auth = new FakeAuth({ configured: false });
    const { user } = renderApp({ auth, api: new FakeApi(), route: '/login' });

    await user.type(
      await screen.findByLabelText(/Token de acceso/),
      'header.payload.signature',
    );
    await user.click(screen.getByRole('button', { name: 'Usar token' }));

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();
    expect(auth.token).toBe('header.payload.signature');
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('redirects an anonymous visitor away from a protected screen', async () => {
    renderApp({
      auth: new FakeAuth({ token: null }),
      api: new FakeApi(),
      route: '/documents',
    });

    expect(await screen.findByRole('heading', { name: 'Iniciar sesión' })).toBeInTheDocument();
  });

  it('signs out from the account menu', async () => {
    const { user } = renderApp({ api: new FakeApi(), route: '/documents' });

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Cuenta de Ana Editora' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Cerrar sesión' }));

    expect(await screen.findByRole('heading', { name: 'Iniciar sesión' })).toBeInTheDocument();
  });

  it('is operable with the keyboard alone', async () => {
    const { user } = renderApp({ auth: new FakeAuth(), api: new FakeApi(), route: '/login' });

    const email = await screen.findByLabelText(/Correo electrónico/);
    email.focus();
    expect(email).toHaveFocus();

    await user.keyboard('ana@example.test');
    await user.tab();
    expect(screen.getByLabelText(/Contraseña/)).toHaveFocus();

    await user.keyboard('clave-segura');
    await user.tab();
    expect(screen.getByRole('button', { name: 'Entrar' })).toHaveFocus();

    await user.keyboard('{Enter}');
    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();
  });
});
