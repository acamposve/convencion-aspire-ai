import Chip from '@mui/material/Chip';
import CircularProgress from '@mui/material/CircularProgress';
import type { ExtractionStatus } from '../lib/api';

const STATUS_PRESENTATION: Record<
  ExtractionStatus,
  { label: string; color: 'default' | 'info' | 'success' | 'error' }
> = {
  NotRequested: { label: 'Pendiente de procesar', color: 'default' },
  Requested: { label: 'Solicitado', color: 'info' },
  InProgress: { label: 'Procesando', color: 'info' },
  Completed: { label: 'Procesado', color: 'success' },
  Failed: { label: 'Fallido', color: 'error' },
};

/**
 * Renders the API's status as a label, never infers it.
 *
 * The raw status is kept as `data-status` and in the title so a failure cannot be silently rendered
 * as a neutral or successful state by a future label map.
 */
export function StatusChip({
  status,
  processing = false,
}: {
  status: ExtractionStatus;
  processing?: boolean;
}) {
  if (processing) {
    return (
      <span data-status="Processing">
        <Chip
          size="small"
          color="info"
          icon={<CircularProgress size={12} color="inherit" />}
          label="Procesando…"
        />
      </span>
    );
  }

  const presentation = STATUS_PRESENTATION[status] ?? {
    label: `Estado ${status}`,
    color: 'default' as const,
  };

  return (
    <span data-status={status} title={`Estado del API: ${status}`}>
      <Chip size="small" color={presentation.color} label={presentation.label} />
    </span>
  );
}
