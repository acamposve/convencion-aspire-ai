import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiError } from '../../lib/errors';
import { FakeApi, FakeAuth, makeDocument, makeText } from '../../test/fakes';
import { renderApp } from '../../test/renderApp';

function renderDocuments(api: FakeApi) {
  return renderApp({
    auth: new FakeAuth({ token: 'fake-token' }),
    api,
    route: '/documents',
  });
}

describe('DocumentsPage', () => {
  it('renders the status of every document honestly', async () => {
    const api = new FakeApi();
    api.documents = [
      makeDocument({ id: 'ok', fileName: 'procesado.pdf', extractionStatus: 'Completed' }),
      makeDocument({
        id: 'failed',
        fileName: 'fallido.pdf',
        extractionStatus: 'Failed',
        extractionAttempts: 1,
        extractionError: 'El PDF no contiene texto extraíble.',
      }),
    ];

    const { container } = renderDocuments(api);

    expect(await screen.findByText('procesado.pdf')).toBeInTheDocument();
    expect(screen.getByText('Procesado')).toBeInTheDocument();
    expect(screen.getByText('Fallido')).toBeInTheDocument();
    expect(screen.getByText('El PDF no contiene texto extraíble.')).toBeInTheDocument();
    expect(container.querySelector('[data-status="Completed"]')).not.toBeNull();
    expect(container.querySelector('[data-status="Failed"]')).not.toBeNull();
  });

  it('offers a retry for a failed document and reflects the new failure', async () => {
    const api = new FakeApi();
    api.documents = [
      makeDocument({
        id: 'retry',
        extractionStatus: 'Failed',
        extractionAttempts: 1,
        extractionError: 'La extracción falló.',
      }),
    ];
    api.extraction = {
      status: 'Failed',
      error: 'La extracción volvió a fallar.',
      attempts: 2,
    };

    const { user } = renderDocuments(api);

    const retry = await screen.findByRole('button', { name: 'Reintentar' });
    expect(retry).toBeEnabled();

    await user.click(retry);

    expect(await screen.findByText('La extracción volvió a fallar.')).toBeInTheDocument();
    expect(api.extractCalls).toEqual(['retry']);
  });

  it('disables retry at the attempt limit and states the limit', async () => {
    const api = new FakeApi();
    api.documents = [
      makeDocument({
        id: 'exhausted',
        extractionStatus: 'Failed',
        extractionAttempts: 3,
        extractionError: 'La extracción falló tres veces.',
      }),
    ];

    renderDocuments(api);

    expect(await screen.findByText('La extracción falló tres veces.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeDisabled();
    expect(screen.getByText('Límite de reintentos alcanzado (3 de 3).')).toBeInTheDocument();
  });

  it('processes a document and opens the extracted text', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'process', fileName: 'contrato.pdf' })];
    api.texts.set('process', makeText('process', 'Contenido real del contrato.'));

    const { user } = renderDocuments(api);

    await user.click(await screen.findByRole('button', { name: 'Procesar' }));

    expect(await screen.findByText('Procesado')).toBeInTheDocument();
    expect(api.extractCalls).toEqual(['process']);

    await user.click(screen.getByRole('button', { name: 'Ver texto' }));

    expect(await screen.findByTestId('document-text')).toHaveTextContent(
      'Contenido real del contrato.',
    );
  });

  it('shows not_found for a completed document whose text is missing, never an empty panel', async () => {
    const api = new FakeApi();
    api.documents = [
      makeDocument({ id: 'no-text', extractionStatus: 'Completed', extractionAttempts: 1 }),
    ];

    const { user } = renderDocuments(api);

    await user.click(await screen.findByRole('button', { name: 'Ver texto' }));

    expect(await screen.findByText('not_found')).toBeInTheDocument();
    expect(screen.getByText('No hay texto extraído para este documento.')).toBeInTheDocument();
  });

  it('shows the API error code when processing is refused', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'conflict' })];
    api.extractError = new ApiError(
      409,
      'invalid_state',
      'This document has already been processed.',
    );

    const { user } = renderDocuments(api);
    await user.click(await screen.findByRole('button', { name: 'Procesar' }));

    expect(await screen.findByText('invalid_state')).toBeInTheDocument();
    expect(screen.getByText(/HTTP 409/)).toBeInTheDocument();
  });

  it('polls while a document reports an in-progress status', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'live', extractionStatus: 'InProgress' })];

    renderDocuments(api);

    await screen.findByRole('heading', { name: 'Documentos' });
    await waitFor(() => expect(api.listCalls.length).toBeGreaterThanOrEqual(2), {
      timeout: 4000,
    });
  });
});
