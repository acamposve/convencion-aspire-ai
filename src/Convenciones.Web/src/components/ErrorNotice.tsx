import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import type { UiError } from '../lib/errors';

/**
 * Renders a failure with the stable code first and a safe detail second.
 *
 * The code is the contract: `unsupported_media_type`, `payload_too_large`, `invalid_state`,
 * `no_profile`. A raw exception message is never displayed.
 */
export function ErrorNotice({
  error,
  title = 'No se pudo completar la operación',
}: {
  error: UiError;
  title?: string;
}) {
  return (
    <Alert severity="error" role="alert" sx={{ mt: 2 }}>
      <AlertTitle>{title}</AlertTitle>
      <Box component="span" sx={{ display: 'block' }}>
        {error.detail}
      </Box>
      <Box
        component="span"
        sx={{ display: 'block', mt: 1, fontFamily: 'ui-monospace, monospace', fontSize: '0.8rem' }}
      >
        Código: <strong>{error.code}</strong>
        {error.status !== undefined ? ` · HTTP ${error.status}` : ''}
      </Box>
      {error.correlationId ? (
        <Box component="span" sx={{ display: 'block', fontSize: '0.75rem' }}>
          Correlación: {error.correlationId}
        </Box>
      ) : null}
    </Alert>
  );
}
