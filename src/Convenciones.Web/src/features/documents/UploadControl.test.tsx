import { act, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MAX_DOCUMENT_BYTES } from '../../lib/api';
import { ApiError } from '../../lib/errors';
import { FakeApi, FakeAuth, asUploadedDocument, deferred, makeDocument } from '../../test/fakes';
import { renderApp } from '../../test/renderApp';
import type { UploadedDocument } from '../../lib/api';

async function renderDocuments(api: FakeApi) {
  const result = renderApp({
    auth: new FakeAuth({ token: 'fake-token' }),
    api,
    route: '/documents',
  });
  await screen.findByRole('heading', { name: 'Documentos' });
  return result;
}

function pdfFile(name = 'contrato.pdf', type = 'application/pdf'): File {
  return new File(['%PDF-1.7 contenido'], name, { type });
}

describe('UploadControl', () => {
  it('states the PDF-only 20 MiB rule on the control itself', async () => {
    await renderDocuments(new FakeApi());

    expect(await screen.findByText('PDF únicamente · máximo 20 MiB')).toBeInTheDocument();
    expect(screen.getByText(/20 MiB \(20\.971\.520 bytes\)/)).toBeInTheDocument();
  });

  it('rejects a non-PDF before sending it', async () => {
    const api = new FakeApi();
    const { user } = await renderDocuments(api);

    await user.upload(
      screen.getByTestId('file-input'),
      new File(['texto'], 'notas.txt', { type: 'text/plain' }),
    );

    expect(await screen.findByText('invalid_file_type')).toBeInTheDocument();
    expect(screen.getByText(/Solo se aceptan documentos PDF/)).toBeInTheDocument();
    expect(api.uploadCalls).toHaveLength(0);
  });

  it('rejects a file that claims to be a PDF but declares another type', async () => {
    const api = new FakeApi();
    const { user } = await renderDocuments(api);

    await user.upload(
      screen.getByTestId('file-input'),
      new File(['imagen'], 'foto.pdf', { type: 'image/png' }),
    );

    expect(await screen.findByText('invalid_file_type')).toBeInTheDocument();
    expect(api.uploadCalls).toHaveLength(0);
  });

  it('rejects a file over 20 MiB before sending it and states the limit', async () => {
    const api = new FakeApi();
    const { user } = await renderDocuments(api);

    const tooLarge = pdfFile('grande.pdf');
    Object.defineProperty(tooLarge, 'size', { value: MAX_DOCUMENT_BYTES + 1 });

    await user.upload(screen.getByTestId('file-input'), tooLarge);

    expect(await screen.findByText('file_too_large')).toBeInTheDocument();
    expect(screen.getByText(/20\.0 MiB/)).toBeInTheDocument();
    expect(screen.getByText(/20\.971\.520 bytes/)).toBeInTheDocument();
    expect(api.uploadCalls).toHaveLength(0);
  });

  it('shows upload progress and adds the document in its real status', async () => {
    const api = new FakeApi();
    const pending = deferred<UploadedDocument>();
    const document = makeDocument({ id: 'uploaded', fileName: 'progreso.pdf' });

    api.uploadHandler = async (_file, onProgress) => {
      onProgress(37);
      const result = await pending.promise;
      api.documents = [document, ...api.documents];
      return result;
    };

    const { user } = await renderDocuments(api);

    await user.upload(screen.getByTestId('file-input'), pdfFile('progreso.pdf', 'application/pdf'));

    expect(await screen.findByText(/Subiendo… 37%/)).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Progreso de la subida' })).toHaveAttribute(
      'aria-valuenow',
      '37',
    );

    await act(async () => {
      pending.resolve(asUploadedDocument(document));
    });

    expect(await screen.findByText('Pendiente de procesar')).toBeInTheDocument();
    expect(api.uploadCalls).toHaveLength(1);
  });

  it('displays the API 415 code verbatim when the server rejects the type', async () => {
    const api = new FakeApi();
    api.uploadError = new ApiError(
      415,
      'unsupported_media_type',
      'The file is not a PDF. Only PDF documents are accepted.',
    );

    const { user } = await renderDocuments(api);

    await user.upload(screen.getByTestId('file-input'), pdfFile());
    expect(await screen.findByText('unsupported_media_type')).toBeInTheDocument();
    expect(screen.getByText(/HTTP 415/)).toBeInTheDocument();
  });

  it('displays the API 413 code verbatim when the server rejects the size', async () => {
    const api = new FakeApi();
    api.uploadError = new ApiError(
      413,
      'payload_too_large',
      'The upload exceeds the maximum of 20971520 bytes (20 MiB).',
    );

    const { user } = await renderDocuments(api);

    await user.upload(screen.getByTestId('file-input'), pdfFile());
    expect(await screen.findByText('payload_too_large')).toBeInTheDocument();
    expect(screen.getByText(/HTTP 413/)).toBeInTheDocument();
  });
});
