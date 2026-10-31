import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { App } from '../App';
import type { AppServices } from '../services';
import { FakeApi, FakeAuth, FakePublicApi } from './fakes';

export interface RenderAppOptions {
  auth?: FakeAuth;
  api?: FakeApi;
  publicApi?: FakePublicApi;
  route?: string;
}

/**
 * Renders the real application with fake boundaries.
 *
 * Only `AuthClient`, `ApiClient` and `PublicApi` are replaced; routing, the session provider, the shell
 * and every screen are the production components, so a test cannot pass against a simplified
 * application.
 */
export function renderApp(options: RenderAppOptions = {}) {
  const auth = options.auth ?? new FakeAuth({ token: 'fake-token' });
  const api = options.api ?? new FakeApi();
  const publicApi = options.publicApi ?? new FakePublicApi();
  const route = options.route ?? '/documents';
  const services: AppServices = { auth, api, publicApi };
  const user = userEvent.setup({ applyAccept: false });

  const view = render(
    <MemoryRouter initialEntries={[route]}>
      <App services={services} />
    </MemoryRouter>,
  );

  return { ...view, auth, api, publicApi, user };
}
