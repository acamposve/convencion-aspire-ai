import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FakeApi, FakeAuth, makeDocument } from '../../test/fakes';
import { ApiError } from '../../lib/errors';
import { renderApp } from '../../test/renderApp';

function renderDocuments(api: FakeApi) {
  return renderApp({
    auth: new FakeAuth({ token: 'fake-token' }),
    api,
    route: '/documents',
  });
}

function run(overrides: Partial<import('../../lib/api').ProcessingRun> = {}) {
  return {
    id: 'run-1',
    documentId: 'doc-1',
    status: 'Completed' as const,
    stage: 'Completed',
    attempts: 1,
    clauseCount: 1,
    classifiedCount: 1,
    unclassifiedCount: 0,
    errorCode: null as string | null,
    errorMessage: null as string | null,
    createdAt: '2026-01-15T10:02:00Z',
    startedAt: '2026-01-15T10:02:00Z',
    finishedAt: '2026-01-15T10:03:00Z',
    ...overrides,
  };
}

describe('ClauseProcessingDialog', () => {
  it('shows clauses and records a review decision', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-1', fileName: 'contrato.pdf', extractionStatus: 'Completed' })];
    api.processingRuns.set('doc-1', run());
    api.processingClauses.set('doc-1', [
      {
        id: 'clause-1',
        ordinal: 1,
        number: 'Primera',
        heading: 'Objeto',
        text: 'Cláusula primera. Objeto del contrato.',
        pageFrom: 1,
        pageTo: 1,
        sourceCharStart: 0,
        sourceCharEnd: 40,
        confidence: 0.9,
        needsReview: false,
        reviewStatus: 'Pending',
        classifications: [
          {
            key: 'pricing',
            name: 'Precio y pago',
            confidence: 0.9,
            isPrimary: true,
            rationale: null,
            source: 'ai' as const,
          },
        ],
        classificationVersion: 'v0',
      },
    ]);

    const { user } = renderDocuments(api);

    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));

    expect(await screen.findByText(/Cláusula primera. Objeto del contrato./)).toBeInTheDocument();
    expect(screen.getByText('Precio y pago (90%) · IA')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Aceptar' }));

    await waitFor(() => expect(screen.getByText('Accepted')).toBeInTheDocument());
  });

  it('corrects a classification without hiding the AI source in the UI contract', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-1', fileName: 'contrato.pdf', extractionStatus: 'Completed' })];
    api.processingRuns.set('doc-1', run());
    api.processingClauses.set('doc-1', [
      {
        id: 'clause-1',
        ordinal: 1,
        number: 'Primera',
        heading: 'Objeto',
        text: 'Cláusula primera. Objeto del contrato.',
        pageFrom: 1,
        pageTo: 1,
        sourceCharStart: 0,
        sourceCharEnd: 40,
        confidence: 0.9,
        needsReview: false,
        reviewStatus: 'Pending',
        classifications: [
          {
            key: 'pricing',
            name: 'Precio y pago',
            confidence: 0.9,
            isPrimary: true,
            rationale: null,
            source: 'ai' as const,
          },
        ],
        classificationVersion: 'v0',
      },
    ]);

    const { user } = renderDocuments(api);

    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));
    await user.click(await screen.findByRole('button', { name: 'Corregir clasificación' }));
    await user.click(
      await screen.findByRole('checkbox', { name: /Alcance y obligaciones/ }),
    );
    await user.click(screen.getByRole('radio', { name: 'Alcance y obligaciones' }));
    await user.click(screen.getByRole('button', { name: 'Guardar corrección' }));

    expect(
      await screen.findByText(/Alcance y obligaciones \(100%\) · humana/),
    ).toBeInTheDocument();
    const stored = api.processingClauses.get('doc-1')?.[0];
    expect(stored?.reviewStatus).toBe('Corrected');
    expect(stored?.classifications.some((item) => item.source === 'human')).toBe(true);
  });

  it('explains a processing failure in Spanish', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-2', fileName: 'escaneado.pdf', extractionStatus: 'Completed' })];
    api.processingRuns.set(
      'doc-2',
      run({ id: 'run-2', documentId: 'doc-2', status: 'Failed', stage: 'Failed', errorCode: 'ocr_required' }),
    );

    const { user } = renderDocuments(api);

    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));

    expect(
      await screen.findByText(/El PDF no tiene texto suficiente; se requiere OCR/),
    ).toBeInTheDocument();
  });

  it('reaches clauses beyond the first hundred and keeps a decision across pages', async () => {
    const api = new FakeApi();
    api.documents = [
      makeDocument({ id: 'doc-101', fileName: 'grande.pdf', extractionStatus: 'Completed' }),
    ];
    api.processingRuns.set(
      'doc-101',
      run({ documentId: 'doc-101', clauseCount: 101, classifiedCount: 101 }),
    );
    api.processingClauses.set(
      'doc-101',
      Array.from({ length: 101 }, (_, index) => {
        const ordinal = index + 1;
        return {
          id: `clause-${ordinal}`,
          ordinal,
          number: `Cl\u00e1usula ${ordinal}`,
          heading: null,
          text: `Texto ${ordinal}`,
          pageFrom: 1,
          pageTo: 1,
          sourceCharStart: index * 10,
          sourceCharEnd: index * 10 + 5,
          confidence: 0.9,
          needsReview: false,
          reviewStatus: 'Pending' as const,
          classifications: [
            {
              key: 'pricing',
              name: 'Precio y pago',
              confidence: 0.9,
              isPrimary: true,
              rationale: null,
              source: 'ai' as const,
            },
          ],
          classificationVersion: 'v0',
        };
      }),
    );

    const { user } = renderDocuments(api);

    await user.click(await screen.findByRole('button', { name: 'Procesar cl\u00e1usulas' }));
    expect(await screen.findByText('Texto 1')).toBeInTheDocument();
    expect(screen.queryByText('Texto 101')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Go to next page' }));
    expect(await screen.findByText('Texto 101')).toBeInTheDocument();

    const acceptButtons = screen.getAllByRole('button', { name: 'Aceptar' });
    await user.click(acceptButtons[acceptButtons.length - 1]!);
    await waitFor(() => expect(screen.getByText('Accepted')).toBeInTheDocument());

    // Navigating away and back reloads from the API; the persisted decision is still there.
    await user.click(screen.getByRole('button', { name: 'Go to previous page' }));
    await user.click(screen.getByRole('button', { name: 'Go to next page' }));
    await waitFor(() => expect(screen.getByText('Accepted')).toBeInTheDocument());

    expect(api.processingClauses.get('doc-101')?.at(-1)?.reviewStatus).toBe('Accepted');
  });

  it('does not start processing when the run query fails', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-err', fileName: 'error.pdf', extractionStatus: 'Completed' })];
    api.processingQueryErrors.set(
      'doc-err',
      new ApiError(500, 'internal_error', 'El servidor no respondió.'),
    );

    const { user } = renderDocuments(api);
    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));

    expect(await screen.findByText('El servidor no respondió.')).toBeInTheDocument();
    expect(api.processingCalls).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeInTheDocument();
  });

  it('does not start processing when the caller is not allowed', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-forbidden', fileName: 'x.pdf', extractionStatus: 'Completed' })];
    api.processingQueryErrors.set(
      'doc-forbidden',
      new ApiError(403, 'forbidden', 'No tiene permiso para ver este documento.'),
    );

    const { user } = renderDocuments(api);
    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));

    expect(await screen.findByText('No tiene permiso para ver este documento.')).toBeInTheDocument();
    expect(api.processingCalls).toHaveLength(0);
  });

  it('starts only when the run is confirmed absent', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-new', fileName: 'nuevo.pdf', extractionStatus: 'Completed' })];

    const { user } = renderDocuments(api);
    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));

    await waitFor(() => expect(api.processingCalls).toContain('doc-new'));
  });

  it('retries a transient failure without starting work', async () => {
    const api = new FakeApi();
    api.documents = [makeDocument({ id: 'doc-retry', fileName: 'retry.pdf', extractionStatus: 'Completed' })];
    api.processingRuns.set('doc-retry', run({ documentId: 'doc-retry' }));
    api.processingQueryErrors.set(
      'doc-retry',
      new ApiError(500, 'internal_error', 'Fallo temporal.'),
    );

    const { user } = renderDocuments(api);
    await user.click(await screen.findByRole('button', { name: 'Procesar cláusulas' }));
    expect(await screen.findByText('Fallo temporal.')).toBeInTheDocument();

    api.processingQueryErrors.clear();
    await user.click(screen.getByRole('button', { name: 'Reintentar' }));

    await waitFor(() => expect(screen.queryByText('Fallo temporal.')).not.toBeInTheDocument());
    expect(api.processingCalls).toHaveLength(0);
  });
});
