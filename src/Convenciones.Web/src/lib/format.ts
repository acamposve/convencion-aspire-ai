const MIB = 1024 * 1024;

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return '—';
  }

  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < MIB) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / MIB).toFixed(1)} MiB`;
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  return new Intl.DateTimeFormat('es', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date);
}

export function shortId(value: string): string {
  return value.length > 8 ? value.slice(0, 8) : value;
}
