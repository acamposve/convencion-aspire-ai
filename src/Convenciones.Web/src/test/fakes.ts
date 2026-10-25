import type { AuthClient } from '../lib/auth';
import { AuthError, ApiError } from '../lib/errors';
import type {
  ApiClient,
  Company,
  DocumentListResponse,
  DocumentSummary,
  DocumentText,
  ExtractionState,
  Me,
  UploadedDocument,
} from '../lib/api';

let documentSequence = 0;

export function makeDocument(overrides: Partial<DocumentSummary> = {}): DocumentSummary {
  documentSequence += 1;

  return {
    id: `document-${documentSequence}`,
    fileName: 'contrato.pdf',
    contentType: 'application/pdf',
    sizeBytes: 2048,
    extractionStatus: 'NotRequested',
    extractionAttempts: 0,
    uploadedAt: '2026-01-15T10:00:00Z',
    extractionError: null,
    ...overrides,
  };
}

export function makeText(documentId: string, content = 'Texto extraído del contrato.'): DocumentText {
  return {
    documentId,
    content,
    charCount: content.length,
    extractedAt: '2026-01-15T10:05:00Z',
  };
}

/** The upload response has the checksum and `createdAt`, unlike the list projection. */
export function asUploadedDocument(document: DocumentSummary): UploadedDocument {
  return {
    id: document.id,
    fileName: document.fileName,
    contentType: document.contentType,
    sizeBytes: document.sizeBytes,
    checksumSha256: null,
    extractionStatus: document.extractionStatus,
    createdAt: document.uploadedAt,
  };
}

export interface Deferred<T> {
  promise: Promise<T>;
  resolve(value: T): void;
  reject(reason: unknown): void;
}

export function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });

  return { promise, resolve, reject };
}

export class FakeAuth implements AuthClient {
  readonly isConfigured: boolean;
  token: string | null;
  signInError: AuthError | null;
  signedInWith: { email: string; password: string } | null = null;

  constructor(
    options: {
      configured?: boolean;
      token?: string | null;
      signInError?: AuthError | null;
    } = {},
  ) {
    this.isConfigured = options.configured ?? true;
    this.token = options.token ?? null;
    this.signInError = options.signInError ?? null;
  }

  async signIn(email: string, password: string): Promise<void> {
    if (this.signInError) {
      throw this.signInError;
    }

    this.signedInWith = { email, password };
    this.token = 'fake-token';
  }

  async signInWithToken(token: string): Promise<void> {
    if (token.trim() === '') {
      throw new AuthError('validation_failed');
    }

    this.token = token.trim();
  }

  async signOut(): Promise<void> {
    this.token = null;
  }

  async getAccessToken(): Promise<string | null> {
    return this.token;
  }
}

export class FakeApi implements ApiClient {
  me: Me = {
    userId: 'user-1',
    email: 'ana@example.test',
    displayName: 'Ana Editora',
    companyId: 'company-1',
    capabilities: ['document.read', 'document.write'],
  };

  meError: ApiError | null = null;
  companyError: ApiError | null = null;
  companyName = 'Empresa Demo';
  documents: DocumentSummary[] = [];
  listError: ApiError | null = null;
  uploadError: ApiError | null = null;
  extractError: ApiError | null = null;
  textError: ApiError | null = null;
  extraction: Partial<ExtractionState> | null = null;
  texts = new Map<string, DocumentText>();

  listCalls: Array<{ page: number; pageSize: number }> = [];
  uploadCalls: File[] = [];
  extractCalls: string[] = [];
  textCalls: string[] = [];

  uploadHandler:
    | ((file: File, onProgress: (percent: number) => void) => Promise<UploadedDocument>)
    | null = null;

  async getMe(): Promise<Me> {
    if (this.meError) {
      throw this.meError;
    }

    return this.me;
  }

  async getCompany(id: string): Promise<Company> {
    if (this.companyError) {
      throw this.companyError;
    }

    return { id, name: this.companyName };
  }

  async listDocuments(page: number, pageSize: number): Promise<DocumentListResponse> {
    this.listCalls.push({ page, pageSize });

    if (this.listError) {
      throw this.listError;
    }

    const start = (page - 1) * pageSize;

    return {
      items: this.documents.slice(start, start + pageSize),
      page,
      pageSize,
      total: this.documents.length,
    };
  }

  async uploadDocument(
    file: File,
    onProgress: (percent: number) => void,
  ): Promise<UploadedDocument> {
    this.uploadCalls.push(file);

    if (this.uploadHandler) {
      return this.uploadHandler(file, onProgress);
    }

    onProgress(50);
    onProgress(100);

    if (this.uploadError) {
      throw this.uploadError;
    }

    const document = makeDocument({ fileName: file.name, sizeBytes: file.size });
    this.documents = [document, ...this.documents];
    return asUploadedDocument(document);
  }

  async extractDocument(id: string): Promise<ExtractionState> {
    this.extractCalls.push(id);

    if (this.extractError) {
      throw this.extractError;
    }

    const index = this.documents.findIndex((document) => document.id === id);
    const current = index >= 0 ? this.documents[index] : undefined;

    const result: ExtractionState = {
      id,
      status: 'Completed',
      attempts: (current?.extractionAttempts ?? 0) + 1,
      startedAt: '2026-01-15T10:01:00Z',
      completedAt: '2026-01-15T10:01:02Z',
      error: null,
      charCount: 42,
      ...this.extraction,
    };

    if (index >= 0 && current) {
      this.documents = this.documents.map((document) =>
        document.id === id
          ? {
              ...document,
              extractionStatus: result.status,
              extractionAttempts: result.attempts,
              extractionError: result.error,
            }
          : document,
      );
    }

    return result;
  }

  async getDocument(id: string): Promise<import('../lib/api').DocumentDetail> {
    const document = this.documents.find((candidate) => candidate.id === id);

    if (!document) {
      throw new ApiError(404, 'not_found', 'No document');
    }

    return {
      ...document,
      extractionStartedAt: null,
      extractionCompletedAt: null,
      extractionError: document.extractionError,
      rowVersion: 1,
    };
  }

  async getDocumentText(id: string): Promise<DocumentText> {
    this.textCalls.push(id);

    if (this.textError) {
      throw this.textError;
    }

    const text = this.texts.get(id);

    if (!text) {
      throw new ApiError(404, 'not_found', 'No extracted text exists for this document.');
    }

    return text;
  }
}
