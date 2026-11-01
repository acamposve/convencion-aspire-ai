import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { StatusChip } from '../../components/StatusChip';
import type { DocumentSummary } from '../../lib/api';
import { formatBytes, formatDateTime } from '../../lib/format';

const MAX_ATTEMPTS = 3;

/**
 * One document row.
 *
 * The status is rendered from the API verbatim; a `Failed` document always shows its recorded
 * reason, and retry is disabled once the API's attempt limit is reached. Nothing is inferred from
 * "no error shown" — absence of text is its own state.
 */
export function DocumentCard({
  document,
  processing,
  onProcess,
  onViewText,
  onProcessClauses,
}: {
  document: DocumentSummary;
  processing: boolean;
  onProcess: (document: DocumentSummary) => void;
  onViewText: (document: DocumentSummary) => void;
  onProcessClauses: (document: DocumentSummary) => void;
}) {
  const attemptsExhausted = document.extractionAttempts >= MAX_ATTEMPTS;
  const isCompleted = document.extractionStatus === 'Completed';
  const isLive =
    document.extractionStatus === 'Requested' || document.extractionStatus === 'InProgress';
  const canProcess = !isCompleted && !attemptsExhausted && !processing && !isLive;

  return (
    <Paper component="li" variant="outlined" sx={{ p: { xs: 2, sm: 2.5 }, listStyle: 'none' }}>
      <Stack spacing={1.5}>
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1}
          justifyContent="space-between"
          alignItems={{ xs: 'flex-start', sm: 'center' }}
        >
          <Box sx={{ minWidth: 0 }}>
            <Typography
              variant="subtitle1"
              component="h2"
              sx={{ fontWeight: 600, overflowWrap: 'anywhere' }}
            >
              {document.fileName}
            </Typography>
            <Typography variant="body2" color="text.secondary">
              {formatBytes(document.sizeBytes)} · subido el {formatDateTime(document.uploadedAt)}
            </Typography>
          </Box>

          <Stack direction="row" spacing={1} alignItems="center" useFlexGap flexWrap="wrap">
            <StatusChip status={document.extractionStatus} processing={processing} />
            <Typography
              variant="caption"
              color="text.secondary"
              sx={{ fontFamily: 'ui-monospace, monospace' }}
            >
              API: {document.extractionStatus}
            </Typography>
          </Stack>
        </Stack>

        {document.extractionStatus === 'Failed' ? (
          <Box
            sx={{
              borderLeft: 4,
              borderColor: 'error.main',
              pl: 1.5,
              py: 0.5,
              bgcolor: 'rgba(179, 38, 30, 0.06)',
              borderRadius: 1,
            }}
          >
            <Typography variant="body2" color="error" role="alert" data-testid="extraction-error">
              {document.extractionError ?? 'La extracción falló y el API no registró un motivo.'}
            </Typography>
          </Box>
        ) : null}

        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1}
          alignItems={{ xs: 'stretch', sm: 'center' }}
        >
          {isCompleted ? (
            <>
              <Button variant="contained" size="small" onClick={() => onViewText(document)}>
                Ver texto
              </Button>
              <Button variant="outlined" size="small" onClick={() => onProcessClauses(document)}>
                Procesar cláusulas
              </Button>
            </>
          ) : (
            <Button
              variant="contained"
              size="small"
              disabled={!canProcess}
              onClick={() => onProcess(document)}
              startIcon={
                processing || isLive ? (
                  <CircularProgress size={16} color="inherit" />
                ) : undefined
              }
              data-testid={`process-${document.id}`}
            >
              {processing || isLive
                ? 'Procesando…'
                : document.extractionStatus === 'Failed'
                  ? 'Reintentar'
                  : 'Procesar'}
            </Button>
          )}

          {attemptsExhausted && !isCompleted ? (
            <Typography variant="caption" color="error">
              Límite de reintentos alcanzado ({MAX_ATTEMPTS} de {MAX_ATTEMPTS}).
            </Typography>
          ) : null}

          {document.extractionStatus === 'Failed' ? (
            <Typography variant="caption" color="text.secondary">
              Intentos: {document.extractionAttempts} de {MAX_ATTEMPTS}
            </Typography>
          ) : null}
        </Stack>
      </Stack>
    </Paper>
  );
}
