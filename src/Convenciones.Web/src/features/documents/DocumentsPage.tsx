import { useCallback, useEffect, useState } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import LinearProgress from '@mui/material/LinearProgress';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useServices } from '../../services';
import { describeError, type UiError } from '../../lib/errors';
import type { DocumentListResponse, DocumentSummary, UploadedDocument } from '../../lib/api';
import { ErrorNotice } from '../../components/ErrorNotice';
import { UploadControl } from './UploadControl';
import { DocumentCard } from './DocumentCard';
import { TextDialog } from './TextDialog';
import { ClauseProcessingDialog } from './ClauseProcessingDialog';

const PAGE_SIZE = 10;

/**
 * The document workflow: upload, see the honest status, process, read the text.
 *
 * The backend's extraction is synchronous (T5), so an upload is `NotRequested` and processing is an
 * explicit action. While any document reports `Requested` or `InProgress`, the list is refreshed
 * every two seconds; the UI never invents a terminal state, it re-reads the API.
 */
export function DocumentsPage() {
  const { api } = useServices();
  const [page, setPage] = useState(1);
  const [data, setData] = useState<DocumentListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [listError, setListError] = useState<UiError | null>(null);
  const [actionError, setActionError] = useState<UiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [processingId, setProcessingId] = useState<string | null>(null);
  const [selected, setSelected] = useState<DocumentSummary | null>(null);
  const [clausesDocument, setClausesDocument] = useState<DocumentSummary | null>(null);

  const load = useCallback(
    async (targetPage: number, quiet = false) => {
      if (!quiet) {
        setLoading(true);
      }

      setListError(null);

      try {
        const response = await api.listDocuments(targetPage, PAGE_SIZE);
        setData(response);
      } catch (failure) {
        setListError(describeError(failure));
      } finally {
        if (!quiet) {
          setLoading(false);
        }
      }
    },
    [api],
  );

  useEffect(() => {
    void load(page);
  }, [load, page]);

  const hasLiveStatus =
    data?.items.some(
      (document) =>
        document.extractionStatus === 'Requested' || document.extractionStatus === 'InProgress',
    ) ?? false;

  useEffect(() => {
    if (!hasLiveStatus) {
      return;
    }

    const timer = window.setInterval(() => {
      void load(page, true);
    }, 2000);

    return () => window.clearInterval(timer);
  }, [hasLiveStatus, load, page]);

  const handleUploaded = async (document: UploadedDocument) => {
    setActionError(null);
    setNotice(`Documento «${document.fileName}» subido. Pulse Procesar para extraer el texto.`);

    if (page === 1) {
      await load(1, true);
    } else {
      setPage(1);
    }
  };

  const handleProcess = async (document: DocumentSummary) => {
    setProcessingId(document.id);
    setActionError(null);
    setNotice(null);

    try {
      const result = await api.extractDocument(document.id);

      setNotice(
        result.status === 'Completed'
          ? `Extracción completada para «${document.fileName}».`
          : `La extracción de «${document.fileName}» terminó en estado ${result.status}.`,
      );

      await load(page, true);
    } catch (failure) {
      setActionError(describeError(failure));
      // The document may have changed even when the request failed (409 after a completed attempt),
      // so the row is re-read instead of guessing its state.
      await load(page, true);
    } finally {
      setProcessingId(null);
    }
  };

  const totalPages = data === null ? 1 : Math.max(1, Math.ceil(data.total / data.pageSize));

  return (
    <Stack spacing={3}>
      <Box>
        <Typography variant="h4" component="h1" gutterBottom>
          Documentos
        </Typography>
        <Typography variant="body1" color="text.secondary">
          Suba contratos colectivos en PDF, procese el texto y consulte el resultado.
        </Typography>
      </Box>

      <UploadControl onUploaded={handleUploaded} />

      {notice ? (
        <Alert severity="success" role="status" onClose={() => setNotice(null)}>
          {notice}
        </Alert>
      ) : null}

      {actionError ? <ErrorNotice error={actionError} title="La operación no se completó" /> : null}

      <Box>
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          justifyContent="space-between"
          alignItems={{ xs: 'flex-start', sm: 'center' }}
          spacing={1}
          sx={{ mb: 1.5 }}
        >
          <Typography variant="h5" component="h2">
            Sus documentos
          </Typography>
          {data ? (
            <Typography variant="body2" color="text.secondary">
              {data.total.toLocaleString('es')} documento{data.total === 1 ? '' : 's'}
            </Typography>
          ) : null}
        </Stack>

        {loading && data === null ? (
          <LinearProgress aria-label="Cargando documentos" />
        ) : null}

        {listError ? (
          <Box>
            <ErrorNotice error={listError} title="No se pudo cargar la lista" />
            <Button sx={{ mt: 1 }} variant="outlined" onClick={() => void load(page)}>
              Reintentar
            </Button>
          </Box>
        ) : null}

        {data !== null && data.items.length === 0 && !listError ? (
          <Paper variant="outlined" sx={{ p: 3, textAlign: 'center' }}>
            <Typography variant="body1" gutterBottom>
              Aún no hay documentos.
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Suba un contrato en PDF para comenzar.
            </Typography>
          </Paper>
        ) : null}

        {data !== null && data.items.length > 0 ? (
          <>
            <Stack
              component="ul"
              spacing={2}
              data-testid="document-list"
              sx={{ listStyle: 'none', p: 0, m: 0 }}
            >
              {data.items.map((document) => (
                <DocumentCard
                  key={document.id}
                  document={document}
                  processing={processingId === document.id}
                  onProcess={(candidate) => void handleProcess(candidate)}
                  onViewText={setSelected}
                  onProcessClauses={setClausesDocument}
                />
              ))}
            </Stack>

            {totalPages > 1 ? (
              <Stack
                direction={{ xs: 'column', sm: 'row' }}
                spacing={1}
                alignItems={{ xs: 'stretch', sm: 'center' }}
                justifyContent="center"
                sx={{ mt: 2 }}
              >
                <Button
                  variant="outlined"
                  disabled={page <= 1 || loading}
                  onClick={() => setPage((current) => Math.max(1, current - 1))}
                >
                  Anterior
                </Button>
                <Typography variant="body2" color="text.secondary" textAlign="center">
                  Página {page} de {totalPages}
                </Typography>
                <Button
                  variant="outlined"
                  disabled={page >= totalPages || loading}
                  onClick={() => setPage((current) => Math.min(totalPages, current + 1))}
                >
                  Siguiente
                </Button>
              </Stack>
            ) : null}
          </>
        ) : null}
      </Box>

      <TextDialog document={selected} onClose={() => setSelected(null)} />
      <ClauseProcessingDialog
        document={clausesDocument}
        open={clausesDocument !== null}
        onClose={() => setClausesDocument(null)}
      />
    </Stack>
  );
}
