import { useEffect, useState } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Typography from '@mui/material/Typography';
import { useServices } from '../../services';
import { describeError, type UiError } from '../../lib/errors';
import type { DocumentSummary, DocumentText } from '../../lib/api';
import { ErrorNotice } from '../../components/ErrorNotice';

/**
 * The extracted-text viewer.
 *
 * "No text" is a 404 with `not_found`, and it is rendered as such. It is never an empty panel that
 * looks like a successful extraction which found nothing.
 */
export function TextDialog({
  document,
  onClose,
}: {
  document: DocumentSummary | null;
  onClose: () => void;
}) {
  const { api } = useServices();
  const [text, setText] = useState<DocumentText | null>(null);
  const [error, setError] = useState<UiError | null>(null);
  const [loading, setLoading] = useState(false);

  const documentId = document?.id ?? null;
  const isCompleted = document?.extractionStatus === 'Completed';

  useEffect(() => {
    if (documentId === null || !isCompleted) {
      setText(null);
      setError(null);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setText(null);
    setError(null);
    setLoading(true);

    void api
      .getDocumentText(documentId)
      .then((result) => {
        if (!cancelled) {
          setText(result);
        }
      })
      .catch((failure) => {
        if (!cancelled) {
          setError(describeError(failure));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [api, documentId, isCompleted]);

  return (
    <Dialog
      open={document !== null}
      onClose={onClose}
      fullWidth
      maxWidth="md"
      aria-labelledby="text-dialog-title"
    >
      <DialogTitle id="text-dialog-title" sx={{ overflowWrap: 'anywhere', pr: 6 }}>
        {document?.fileName ?? 'Texto extraído'}
      </DialogTitle>

      <DialogContent dividers>
        {!isCompleted ? (
          <Alert severity="info">
            Este documento no tiene texto extraído. Estado actual: {document?.extractionStatus ?? '—'}.
          </Alert>
        ) : null}

        {loading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
            <CircularProgress aria-label="Cargando el texto extraído" />
          </Box>
        ) : null}

        {error ? (
          <>
            {error.code === 'not_found' ? (
              <Typography variant="body2" sx={{ mb: 1 }}>
                No hay texto extraído para este documento.
              </Typography>
            ) : null}
            <ErrorNotice error={error} title="No se pudo leer el texto" />
          </>
        ) : null}

        {text ? (
          <>
            <Box
              component="pre"
              className="extracted-text"
              data-testid="document-text"
              tabIndex={0}
              aria-label={`Texto extraído de ${document?.fileName ?? 'el documento'}`}
            >
              {text.content}
            </Box>
            <Typography variant="caption" color="text.secondary" sx={{ mt: 1, display: 'block' }}>
              {text.charCount.toLocaleString('es')} caracteres
            </Typography>
          </>
        ) : null}
      </DialogContent>

      <DialogActions>
        <Button onClick={onClose}>Cerrar</Button>
      </DialogActions>
    </Dialog>
  );
}
