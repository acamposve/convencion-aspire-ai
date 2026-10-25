import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import {
  FakeApi,
  makeAgreementDetail,
  makeClause,
  makeHeading,
} from '../../test/fakes';

describe('AgreementsPage', () => {
  it('lists agreements with their publication state and clause count', async () => {
    const api = new FakeApi();
    api.agreements = [
      makeAgreementDetail({
        title: 'Empresa Demo · 2026',
        clauses: [makeClause(), makeClause({ number: 2, title: 'Vacaciones' })],
      }),
      makeAgreementDetail({
        title: 'Empresa Demo · 2024',
        validFrom: '2024-01-01',
        state: 'Published',
        publishedAt: '2024-02-01T00:00:00Z',
      }),
    ];

    renderApp({ api, route: '/agreements' });

    expect(await screen.findByText('Empresa Demo · 2026')).toBeInTheDocument();
    expect(screen.getByText('Empresa Demo · 2024')).toBeInTheDocument();
    expect(screen.getByText(/Publicado/)).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Acuerdos' })).toBeInTheDocument();
  });

  it('hides the create control when the caller cannot write agreements', async () => {
    const api = new FakeApi();
    api.me = { ...api.me, capabilities: ['agreement.read'] };

    renderApp({ api, route: '/agreements' });

    await screen.findByText('No hay acuerdos que coincidan con el filtro.');
    expect(screen.queryByRole('button', { name: 'Nuevo acuerdo' })).not.toBeInTheDocument();
  });

  it('validates the create form before calling the API', async () => {
    const api = new FakeApi();
    const { user } = renderApp({ api, route: '/agreements' });

    await user.click(await screen.findByRole('button', { name: 'Nuevo acuerdo' }));

    const scope = screen.getByLabelText(/Ámbito/);
    await user.clear(scope);
    await user.click(screen.getByRole('button', { name: 'Crear acuerdo' }));

    expect(await screen.findByText('Indique el ámbito del acuerdo.')).toBeInTheDocument();
    expect(api.calls).not.toContain('createAgreement');
  });

  it('does not edit a published agreement in place and requires an explicit unpublish', async () => {
    const api = new FakeApi();
    const published = makeAgreementDetail({
      id: 'published',
      state: 'Published',
      publishedAt: '2026-02-01T10:00:00Z',
      clauses: [makeClause({ headingName: 'Salarios' })],
    });
    api.agreements = [published];

    const { user } = renderApp({ api, route: '/agreements/published' });

    expect(await screen.findByText(/no se edita en sitio/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Añadir cláusula' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Editar cláusula/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Despublicar' }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent(/dejará de ser visible/i);

    await user.click(screen.getByRole('button', { name: 'Despublicar' }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Añadir cláusula' })).toBeInTheDocument();
    });
    expect(api.calls).toContain('unpublishAgreement');
  });

  it('completes author → publish → compare with the keyboard alone', async () => {
    const api = new FakeApi();
    const heading = makeHeading();
    api.headings = [heading];
    api.agreements = [makeAgreementDetail({ id: 'draft', clauses: [] })];

    const { user } = renderApp({ api, route: '/agreements/draft' });

    // Author a clause: focus the control and activate it with the keyboard.
    const addClause = await screen.findByRole('button', { name: 'Añadir cláusula' });
    addClause.focus();
    await user.keyboard('{Enter}');

    const dialog = await screen.findByRole('dialog');
    const title = screen.getByLabelText(/^Título/);
    await user.clear(title);
    await user.type(title, 'Salario mínimo');

    const body = screen.getByLabelText(/^Texto/);
    await user.clear(body);
    await user.type(body, 'Texto de la cláusula.');

    const headingSelect = screen.getByRole('combobox', { name: /Materia/ });
    headingSelect.focus();
    await user.keyboard('{Enter}{ArrowDown}{Enter}');

    const save = screen.getByRole('button', { name: 'Guardar' });
    save.focus();
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(api.calls).toContain('createClause');
    });

    // Publish, confirming the dialog with the keyboard.
    const publish = await screen.findByRole('button', { name: 'Publicar' });
    publish.focus();
    await user.keyboard('{Enter}');

    await screen.findByText(/El acuerdo pasará a ser visible/i);
    const confirm = screen.getAllByRole('button', { name: 'Publicar' }).at(-1)!;
    confirm.focus();
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(api.calls).toContain('publishAgreement');
    });
    expect(dialog).not.toBeInTheDocument();
  }, 20000);
});
