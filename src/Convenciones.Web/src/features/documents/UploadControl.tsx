import { useRef, useState, type DragEvent, type KeyboardEvent } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import LinearProgress from '@mui/material/LinearProgress';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import UploadFileIcon from '@mui/icons-material/UploadFile';
import { useServices } from '../../services';
import { describeError, type UiError } from '../../lib/errors';
import { validatePdfFile } from '../../lib/validation';
import { MAX_DOCUMENT_BYTES } from '../../lib/api';
import type { UploadedDocument } from '../../lib/api';
import { formatBytes } from '../../lib/format';
import { ErrorNotice } from '../../components/ErrorNotice';

/**
 * The upload control, with the format and size stated on the control itself.
 *
 * The browser check is a courtesy; the server parses the bytes and remains authoritative. A failure
 * shows the API's code (`unsupported_media_type`, `payload_too_large`, …), never a raw message.
 */
export function UploadControl({
  onUploaded,
}: {
  onUploaded: (document: UploadedDocument) => void | Promise<void>;
}) {
  const { api } = useServices();
  const inputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<UiError | null>(null);

  const openPicker = () => {
    if (!uploading) {
      inputRef.current?.click();
    }
  };

  const upload = async (file: File) => {
    const rejection = validatePdfFile(file);

    if (rejection !== null) {
      setError(rejection);
      setProgress(0);
      return;
    }

    setError(null);
    setUploading(true);
    setProgress(0);

    try {
      const document = await api.uploadDocument(file, (percent) => setProgress(percent));
      setProgress(100);
      await onUploaded(document);
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setUploading(false);
    }
  };

  const handleFiles = (files: FileList | null) => {
    const file = files?.[0];

    if (file) {
      void upload(file);
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      openPicker();
    }
  };

  const handleDrop = (event: DragEvent<HTMLElement>) => {
    event.preventDefault();

    if (!uploading) {
      handleFiles(event.dataTransfer.files);
    }
  };

  return (
    <Box>
      <Paper
        variant="outlined"
        role="button"
        tabIndex={0}
        aria-label="Subir un documento PDF. PDF únicamente, máximo 20 MiB."
        aria-describedby="upload-limits"
        aria-disabled={uploading}
        className="focus-ring"
        data-testid="upload-dropzone"
        onClick={openPicker}
        onKeyDown={handleKeyDown}
        onDragOver={(event) => event.preventDefault()}
        onDrop={handleDrop}
        sx={{
          p: { xs: 2, sm: 3 },
          borderStyle: 'dashed',
          borderWidth: 2,
          textAlign: 'center',
          cursor: uploading ? 'progress' : 'pointer',
          transition: 'border-color 120ms ease',
          '&:hover': { borderColor: 'primary.main' },
        }}
      >
        <Stack spacing={1} alignItems="center">
          <UploadFileIcon color="primary" fontSize="large" />
          <Typography variant="subtitle1" component="p" sx={{ fontWeight: 600 }}>
            Suba un contrato colectivo
          </Typography>
          <Typography id="upload-limits" variant="body2" color="text.secondary">
            PDF únicamente · máximo 20 MiB
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Arrastre el archivo aquí o pulse para seleccionarlo.
          </Typography>
        </Stack>
      </Paper>

      <input
        ref={inputRef}
        data-testid="file-input"
        type="file"
        accept=".pdf,application/pdf"
        style={{ display: 'none' }}
        disabled={uploading}
        onChange={(event) => {
          const files = event.target.files;
          event.target.value = '';
          handleFiles(files);
        }}
      />

      {uploading ? (
        <Box sx={{ mt: 2 }} aria-live="polite">
          <LinearProgress
            variant="determinate"
            value={progress}
            aria-label="Progreso de la subida"
          />
          <Typography variant="caption" color="text.secondary" sx={{ mt: 0.5, display: 'block' }}>
            Subiendo… {progress}% · máximo {formatBytes(MAX_DOCUMENT_BYTES)}
          </Typography>
        </Box>
      ) : null}

      {error ? <ErrorNotice error={error} title="El archivo no se pudo subir" /> : null}

      {!uploading && error === null ? (
        <Alert severity="info" sx={{ mt: 2 }} role="note">
          El tamaño máximo es de 20 MiB (20.971.520 bytes). El servidor valida el contenido del PDF.
        </Alert>
      ) : null}
    </Box>
  );
}
