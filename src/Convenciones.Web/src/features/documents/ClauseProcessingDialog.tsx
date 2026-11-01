import { useCallback, useEffect, useState } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import LinearProgress from '@mui/material/LinearProgress';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useServices } from '../../services';
import { describeError, type UiError } from '../../lib/errors';
import type {
  ClauseReviewStatus,
  DocumentSummary,
  ProcessingClause,
  ProcessingRun,
} from '../../lib/api';

const POLL_MS = 2000;

const ERROR_COPY: Record<string, string> = {
  text_not_extracted: 'Primero hay que extraer el texto del PDF.',
  ocr_required: 'El PDF no tiene texto suficiente; se requiere OCR, que aún no está disponible.',
  document_too_large: 'El documento es demasiado grande para esta versión.',
  no_clauses_found: 'No se encontraron cláusulas en el documento.',
  invalid_model_output: 'La IA devolvió un resultado inválido; se puede reintentar.',
  provider_unavailable: 'El proveedor de IA no está disponible en este momento.',
  timeout: 'El procesamiento superó el tiempo máximo; se puede reintentar.',
  taxonomy_empty: 'La taxonomía no tiene categorías activas.',
  stage_not_configured: 'El worker aún no tiene configurada la etapa de extracción.',
};

function explain(run: ProcessingRun): string {
  const known = run.errorCode ? ERROR_COPY[run.errorCode] : undefined;
  if (known) {
    return known;
  }

  return run.errorMessage ?? 'El procesamiento falló sin un motivo registrado.';
}

/**
 * Starts the clause pipeline and follows it to completion.
 *
 * The dialog never invents progress: it re-reads the run and only shows clauses once the API says
 * Completed. Review is a separate, explicit action.
 */
export function ClauseProcessingDialog({
  document,
  open,
  onClose,
}: {
  document: DocumentSummary | null;
  open: boolean;
  onClose: () => void;
}) {
  const { api } = useServices();
  const [run, setRun] = useState<ProcessingRun | null>(null);
  const [clauses, setClauses] = useState<ProcessingClause[]>([]);
  const [error, setError] = useState<UiError | null>(null);
  const [busy, setBusy] = useState(false);
  const [reviewing, setReviewing] = useState<string | null>(null);

  const loadClauses = useCallback(
    async (documentId: string) => {
      const page = await api.listClauses(documentId, 1, 100);
      setClauses(page.items);
    },
    [api],
  );

  const refresh = useCallback(
    async (documentId: string) => {
      const current = await api.getProcessing(documentId);
      setRun(current);

      if (current.status === 'Completed') {
        await loadClauses(documentId);
      }

      return current;
    },
    [api, loadClauses],
  );

  useEffect(() => {
    if (!open || document === null) {
      return;
    }

    let cancelled = false;
    let timer: number | undefined;

    const poll = async (documentId: string) => {
      try {
        const current = await refresh(documentId);
        if (cancelled) {
          return;
        }

        if (current.status === 'Queued' || current.status === 'Running') {
          timer = window.setTimeout(() => void poll(documentId), POLL_MS);
        }
      } catch (failure) {
        if (!cancelled) {
          setError(describeError(failure));
        }
      }
    };

    const start = async () => {
      setRun(null);
      setClauses([]);
      setError(null);
      setBusy(true);

      try {
        try {
          const current = await refresh(document.id);
          if (current.status === 'Queued' || current.status === 'Running') {
            timer = window.setTimeout(() => void poll(document.id), POLL_MS);
          }
        } catch {
          await api.startProcessing(document.id);
          await poll(document.id);
        }
      } catch (failure) {
        if (!cancelled) {
          setError(describeError(failure));
        }
      } finally {
        if (!cancelled) {
          setBusy(false);
        }
      }
    };

    void start();

    return () => {
      cancelled = true;
      if (timer !== undefined) {
        window.clearTimeout(timer);
      }
    };
  }, [api, document, open, refresh]);

  const review = async (clause: ProcessingClause, status: ClauseReviewStatus) => {
    setReviewing(clause.id);
    setError(null);

    try {
      const updated = await api.reviewClause(clause.id, status);
      setClauses((current) => current.map((item) => (item.id === updated.id ? updated : item)));
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setReviewing(null);
    }
  };

  const active = run?.status === 'Queued' || run?.status === 'Running';

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md" aria-labelledby="clause-processing-dialog">
      <DialogTitle id="clause-processing-dialog">
        Procesar cláusulas — {document?.fileName ?? ''}
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error.detail}</Alert> : null}

          {busy && run === null ? <LinearProgress aria-label="Iniciando el procesamiento" /> : null}

          {run ? (
            <Box>
              <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
                <Chip label={run.status} color={run.status === 'Failed' ? 'error' : 'default'} />
                <Typography variant="body2" color="text.secondary">
                  Etapa: {run.stage}
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  Cláusulas: {run.clauseCount}
                </Typography>
                {run.classifiedCount + run.unclassifiedCount > 0 ? (
                  <Typography variant="body2" color="text.secondary">
                    Clasificadas: {run.classifiedCount} · Sin clasificar: {run.unclassifiedCount}
                  </Typography>
                ) : null}
              </Stack>

              {active ? (
                <Box sx={{ mt: 1.5 }}>
                  <LinearProgress aria-label="Procesando cláusulas" />
                  <Typography variant="caption" color="text.secondary">
                    El procesamiento continúa en segundo plano; puede cerrar y volver más tarde.
                  </Typography>
                </Box>
              ) : null}

              {run.status === 'Failed' || run.status === 'Cancelled' ? (
                <Alert severity={run.status === 'Failed' ? 'error' : 'warning'} sx={{ mt: 1.5 }}>
                  {run.status === 'Cancelled' ? 'El procesamiento fue cancelado.' : explain(run)}
                </Alert>
              ) : null}
            </Box>
          ) : null}

          {run?.status === 'Completed' && clauses.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              El procesamiento terminó sin cláusulas.
            </Typography>
          ) : null}

          {clauses.length > 0 ? (
            <Stack spacing={2} data-testid="clause-list">
              {clauses.map((clause) => (
                <Box
                  key={clause.id}
                  sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 1.5 }}
                >
                  <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
                    <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                      {clause.number ?? `Cláusula ${clause.ordinal}`}
                      {clause.heading ? ` · ${clause.heading}` : ''}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">
                      Páginas {clause.pageFrom}
                      {clause.pageTo !== clause.pageFrom ? `–${clause.pageTo}` : ''}
                    </Typography>
                    {clause.needsReview ? <Chip size="small" color="warning" label="Revisar" /> : null}
                    {clause.reviewStatus !== 'Pending' ? (
                      <Chip size="small" variant="outlined" label={clause.reviewStatus} />
                    ) : null}
                  </Stack>

                  <Typography variant="body2" sx={{ mt: 1, whiteSpace: 'pre-wrap' }}>
                    {clause.text}
                  </Typography>

                  {clause.classifications.length > 0 ? (
                    <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap sx={{ mt: 1 }}>
                      {clause.classifications.map((classification) => (
                        <Chip
                          key={classification.key}
                          size="small"
                          color={classification.isPrimary ? 'primary' : 'default'}
                          variant={classification.isPrimary ? 'filled' : 'outlined'}
                          label={`${classification.name}${classification.confidence === null ? '' : ` (${Math.round(classification.confidence * 100)}%)`}`}
                        />
                      ))}
                    </Stack>
                  ) : (
                    <Chip size="small" variant="outlined" label="Sin clasificar" sx={{ mt: 1 }} />
                  )}

                  <Stack direction="row" spacing={1} sx={{ mt: 1.5 }}>
                    <Button
                      size="small"
                      variant="outlined"
                      disabled={reviewing === clause.id || clause.reviewStatus === 'Accepted'}
                      onClick={() => void review(clause, 'Accepted')}
                    >
                      Aceptar
                    </Button>
                    <Button
                      size="small"
                      variant="outlined"
                      color="error"
                      disabled={reviewing === clause.id || clause.reviewStatus === 'Rejected'}
                      onClick={() => void review(clause, 'Rejected')}
                    >
                      Rechazar
                    </Button>
                  </Stack>
                </Box>
              ))}
            </Stack>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cerrar</Button>
      </DialogActions>
    </Dialog>
  );
}
