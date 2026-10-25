import { describe, expect, it } from 'vitest';
import { MAX_DOCUMENT_BYTES } from './api';
import { validatePdfFile } from './validation';

function file(overrides: Partial<{ name: string; type: string; size: number }> = {}) {
  return {
    name: 'contrato.pdf',
    type: 'application/pdf',
    size: 1024,
    ...overrides,
  };
}

describe('validatePdfFile', () => {
  it('accepts a PDF within the limit', () => {
    expect(validatePdfFile(file())).toBeNull();
  });

  it('accepts a PDF whose browser did not expose a MIME type', () => {
    expect(validatePdfFile(file({ type: '' }))).toBeNull();
  });

  it('rejects another extension', () => {
    expect(validatePdfFile(file({ name: 'contrato.docx' }))?.code).toBe('invalid_file_type');
  });

  it('rejects another declared type even when the name ends in .pdf', () => {
    expect(validatePdfFile(file({ name: 'foto.pdf', type: 'image/jpeg' }))?.code).toBe(
      'invalid_file_type',
    );
  });

  it('rejects an empty file', () => {
    expect(validatePdfFile(file({ size: 0 }))?.code).toBe('file_empty');
  });

  it('accepts exactly 20 MiB and rejects one byte more', () => {
    expect(validatePdfFile(file({ size: MAX_DOCUMENT_BYTES }))).toBeNull();

    const rejection = validatePdfFile(file({ size: MAX_DOCUMENT_BYTES + 1 }));
    expect(rejection?.code).toBe('file_too_large');
    expect(rejection?.detail).toContain('20.0 MiB');
  });
});
