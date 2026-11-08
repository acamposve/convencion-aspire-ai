import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FakeApi, FakeAuth, makeDocument } from '../../test/fakes';
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
});
