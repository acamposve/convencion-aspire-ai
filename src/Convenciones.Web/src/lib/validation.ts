import { MAX_DOCUMENT_BYTES } from './api';
import { formatBytes } from './format';

export interface FileRejection {
  code: 'invalid_file_type' | 'file_too_large' | 'file_empty';
  detail: string;
}

/**
 * The client-side half of the upload contract: PDF only, at most 20 MiB.
 *
 * The server is authoritative and checks the bytes; this check exists so an obviously wrong file
 * never leaves the browser. The limit is displayed on the control itself, not only after a failure.
 */
export function validatePdfFile(file: Pick<File, 'name' | 'type' | 'size'>): FileRejection | null {
  const nameIsPdf = file.name.toLowerCase().endsWith('.pdf');
  const declaredType = file.type.trim().toLowerCase();
  const typeIsPdf = declaredType === '' || declaredType === 'application/pdf';

  if (!nameIsPdf || !typeIsPdf) {
    return {
      code: 'invalid_file_type',
      detail: 'Solo se aceptan documentos PDF. Seleccione un archivo .pdf.',
    };
  }

  if (file.size === 0) {
    return { code: 'file_empty', detail: 'El archivo está vacío.' };
  }

  if (file.size > MAX_DOCUMENT_BYTES) {
    return {
      code: 'file_too_large',
      detail:
        `El archivo supera el máximo de ${formatBytes(MAX_DOCUMENT_BYTES)} ` +
        `(${MAX_DOCUMENT_BYTES.toLocaleString('es')} bytes).`,
    };
  }

  return null;
}
