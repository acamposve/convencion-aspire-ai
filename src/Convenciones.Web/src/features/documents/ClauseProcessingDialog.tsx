import { useCallback, useEffect, useState } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import LinearProgress from '@mui/material/LinearProgress';
import Pagination from '@mui/material/Pagination';
import Radio from '@mui/material/Radio';
import RadioGroup from '@mui/material/RadioGroup';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useServices } from '../../services';
import { describeError, type UiError } from '../../lib/errors';
import type {
  ClauseCategoryView,
  ClauseReviewStatus,
  DocumentSummary,
  ProcessingClause,
  ProcessingRun,
} from '../../lib/api';

const POLL_MS = 2000;
const PAGE_SIZE = 100;

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
  const [categories, setCategories] = useState<ClauseCategoryView[]>([]);
  const [editing, setEditing] = useState<ProcessingClause | null>(null);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [editedKeys, setEditedKeys] = useState<string[]>([]);
  const [editedPrimary, setEditedPrimary] = useState<string | null>(null);
  const [savingEdit, setSavingEdit] = useState(false);

  const loadClauses = useCallback(
    async (documentId: string, pageNumber: number) => {
      const result = await api.listClauses(documentId, pageNumber, PAGE_SIZE);
      setClauses(result.items);
      setTotal(result.total);
      setPage(result.page);
    },
    [api],
  );

  const refresh = useCallback(
    async (documentId: string, pageNumber = 1) => {
      const current = await api.getProcessing(documentId);
      setRun(current);

      if (current.status === 'Completed') {
        await loadClauses(documentId, pageNumber);
      }

      return current;
    },
    [api, loadClauses],
  );

  const goToPage = async (nextPage: number) => {
    if (document === null) {
      return;
    }

    setError(null);
    try {
      await loadClauses(document.id, nextPage);
    } catch (failure) {
      setError(describeError(failure));
    }
  };

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
      setTotal(0);
      setPage(1);
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

  useEffect(() => {
    if (!open) {
      return;
    }

    let cancelled = false;
    void api
      .listClauseCategories()
      .then((items) => {
        if (!cancelled) {
          setCategories(items);
        }
      })
      .catch((failure: unknown) => {
        if (!cancelled) {
          setError(describeError(failure));
        }
      });

    return () => {
      cancelled = true;
    };
  }, [api, open]);

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

  const beginCorrection = (clause: ProcessingClause) => {
    const keys = clause.classifications.map((item) => item.key);
    setEditedKeys(keys);
    setEditedPrimary(clause.classifications.find((item) => item.isPrimary)?.key ?? keys[0] ?? null);
    setEditing(clause);
    setError(null);
  };

  const toggleKey = (key: string, checked: boolean) => {
    setEditedKeys((current) => {
      const next = checked ? [...new Set([...current, key])] : current.filter((item) => item !== key);
      if (next.length > 0 && (editedPrimary === null || !next.includes(editedPrimary))) {
        setEditedPrimary(next[0] ?? null);
      }
      if (next.length === 0) {
        setEditedPrimary(null);
      }
      return next;
    });
  };

  const saveCorrection = async () => {
    if (editing === null || editedKeys.length === 0) {
      return;
    }

    setSavingEdit(true);
    setError(null);
    try {
      const updated = await api.reviewClause(editing.id, 'Corrected', {
        classifications: editedKeys.map((key) => ({ key, confidence: 1, rationale: null })),
        primary: editedPrimary,
        expectedVersion: editing.classificationVersion,
      });
      setClauses((current) => current.map((item) => (item.id === updated.id ? updated : item)));
      setEditing(null);
    } catch (failure) {
      setError(describeError(failure));
      // A conflict means another correction won. Reload the effective state so the user can retry
      // against the current version instead of resubmitting the stale one.
      if (document) {
        await loadClauses(document.id, page);
      }
    } finally {
      setSavingEdit(false);
    }
  };

  const active = run?.status === 'Queued' || run?.status === 'Running';

  return (
    <>
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
                    <>
                    <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap sx={{ mt: 1 }}>
                      {clause.classifications.map((classification) => (
                        <Chip
                          key={classification.key}
                          size="small"
                          color={classification.isPrimary ? 'primary' : 'default'}
                          variant={classification.isPrimary ? 'filled' : 'outlined'}
                          label={`${classification.name} (${Math.round((classification.confidence ?? 0) * 100)}%)${classification.source === 'human' ? ' · humana' : ' · IA'}`}
                        />
                      ))}
                    </Stack>
                    {clause.classifications
                      .filter((classification) => classification.rationale)
                      .map((classification) => (
                        <Typography
                          key={`${classification.key}-rationale`}
                          variant="caption"
                          color="text.secondary"
                          display="block"
                          sx={{ mt: 0.5 }}
                        >
                          {classification.name}: {classification.rationale}
                        </Typography>
                      ))}
                    </>
                  ) : (
                    <Chip size="small" variant="outlined" label="Sin clasificar" sx={{ mt: 1 }} />
                  )}

                  <Stack direction="row" spacing={1} sx={{ mt: 1.5 }} flexWrap="wrap" useFlexGap>
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
                    <Button
                      size="small"
                      variant="contained"
                      onClick={() => beginCorrection(clause)}
                    >
                      Corregir clasificación
                    </Button>
                  </Stack>
                </Box>
              ))}
            </Stack>
          ) : null}

          {total > PAGE_SIZE ? (
            <Stack spacing={1} alignItems="center" sx={{ mt: 2 }} data-testid="clause-pagination">
              <Pagination
                count={Math.max(1, Math.ceil(total / PAGE_SIZE))}
                page={page}
                onChange={(_, value) => void goToPage(value)}
                color="primary"
                showFirstButton
                showLastButton
              />
              <Typography variant="caption" color="text.secondary">
                Mostrando {clauses.length} de {total} cláusulas
              </Typography>
            </Stack>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cerrar</Button>
      </DialogActions>
    </Dialog>

    <Dialog
      open={editing !== null}
      onClose={() => setEditing(null)}
      fullWidth
      maxWidth="sm"
      aria-labelledby="clause-correction-dialog"
    >
      <DialogTitle id="clause-correction-dialog">
        Corregir clasificación — {editing?.number ?? `cláusula ${editing?.ordinal ?? ''}`}
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography variant="body2" color="text.secondary">
            Seleccione una o varias categorías. La clasificación original de IA permanece registrada
            en la auditoría; esta acción crea una corrección humana.
          </Typography>

          <FormGroup>
            {categories.map((category) => (
              <FormControlLabel
                key={category.key}
                control={
                  <Checkbox
                    checked={editedKeys.includes(category.key)}
                    onChange={(event) => toggleKey(category.key, event.target.checked)}
                  />
                }
                label={`${category.name} (${category.key})`}
              />
            ))}
          </FormGroup>

          <Typography variant="subtitle2">Categoría primaria</Typography>
          <RadioGroup
            value={editedPrimary ?? ''}
            onChange={(event) => setEditedPrimary(event.target.value)}
          >
            {editedKeys.map((key) => {
              const category = categories.find((item) => item.key === key);
              return (
                <FormControlLabel
                  key={key}
                  value={key}
                  control={<Radio />}
                  label={category?.name ?? key}
                />
              );
            })}
          </RadioGroup>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={() => setEditing(null)} disabled={savingEdit}>
          Cancelar
        </Button>
        <Button
          variant="contained"
          onClick={() => void saveCorrection()}
          disabled={savingEdit || editedKeys.length === 0 || editedPrimary === null}
        >
          Guardar corrección
        </Button>
      </DialogActions>
    </Dialog>
    </>
  );
}
