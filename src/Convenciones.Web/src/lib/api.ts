import type { AuthClient } from './auth';
import { ApiError } from './errors';

export const MAX_DOCUMENT_BYTES = 20 * 1024 * 1024;

export interface Me {
  userId: string;
  email: string;
  displayName: string | null;
  companyId: string | null;
  capabilities: string[];
}

export interface Company {
  id: string;
  name: string;
}

export type ExtractionStatus =
  | 'NotRequested'
  | 'Requested'
  | 'InProgress'
  | 'Completed'
  | 'Failed';

export interface DocumentSummary {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  extractionStatus: ExtractionStatus;
  extractionAttempts: number;
  uploadedAt: string;
  extractionError: string | null;
}

/** The `POST /documents` response. It carries the checksum the list projection deliberately omits. */
export interface UploadedDocument {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  checksumSha256: string | null;
  extractionStatus: ExtractionStatus;
  createdAt: string;
}

export interface DocumentDetail extends DocumentSummary {
  extractionStartedAt: string | null;
  extractionCompletedAt: string | null;
  extractionError: string | null;
  rowVersion: number;
}

export interface DocumentListResponse {
  items: DocumentSummary[];
  page: number;
  pageSize: number;
  total: number;
}

export interface ExtractionState {
  id: string;
  status: ExtractionStatus;
  attempts: number;
  startedAt: string | null;
  completedAt: string | null;
  error: string | null;
  charCount: number | null;
}

export interface DocumentText {
  documentId: string;
  content: string;
  charCount: number;
  extractedAt: string;
}

export interface ApiClient {
  getMe(): Promise<Me>;
  getCompany(id: string): Promise<Company>;
  listDocuments(page: number, pageSize: number): Promise<DocumentListResponse>;
  uploadDocument(file: File, onProgress: (percent: number) => void): Promise<UploadedDocument>;
  extractDocument(id: string): Promise<ExtractionState>;
  getDocument(id: string): Promise<DocumentDetail>;
  getDocumentText(id: string): Promise<DocumentText>;
}

interface ProblemDetails {
  code?: unknown;
  detail?: unknown;
  correlationId?: unknown;
}

async function readProblem(response: Response): Promise<ApiError> {
  let problem: ProblemDetails | null = null;

  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    problem = null;
  }

  const code = typeof problem?.code === 'string' ? problem.code : `http_${response.status}`;
  const detail =
    typeof problem?.detail === 'string' ? problem.detail : 'La solicitud no pudo completarse.';
  const correlationId =
    typeof problem?.correlationId === 'string' ? problem.correlationId : undefined;

  return new ApiError(response.status, code, detail, correlationId);
}

function parseProblemJson(text: string): ProblemDetails | null {
  try {
    return JSON.parse(text) as ProblemDetails;
  } catch {
    return null;
  }
}

export class HttpApiClient implements ApiClient {
  private readonly baseUrl: string;

  constructor(
    baseUrl: string,
    private readonly auth: AuthClient,
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
  }

  async getMe(): Promise<Me> {
    return this.request<Me>('/me');
  }

  async getCompany(id: string): Promise<Company> {
    return this.request<Company>(`/companies/${encodeURIComponent(id)}`);
  }

  async listDocuments(page: number, pageSize: number): Promise<DocumentListResponse> {
    const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });

    return this.request<DocumentListResponse>(`/documents?${query.toString()}`);
  }

  async extractDocument(id: string): Promise<ExtractionState> {
    return this.request<ExtractionState>(`/documents/${encodeURIComponent(id)}/extract`, {
      method: 'POST',
    });
  }

  async getDocument(id: string): Promise<DocumentDetail> {
    return this.request<DocumentDetail>(`/documents/${encodeURIComponent(id)}`);
  }

  async getDocumentText(id: string): Promise<DocumentText> {
    return this.request<DocumentText>(`/documents/${encodeURIComponent(id)}/text`);
  }

  uploadDocument(file: File, onProgress: (percent: number) => void): Promise<UploadedDocument> {
    return new Promise<UploadedDocument>((resolve, reject) => {
      void this.auth.getAccessToken().then((token) => {
        if (!token) {
          reject(new ApiError(401, 'unauthenticated', 'No hay una sesión activa.'));
          return;
        }

        const request = new XMLHttpRequest();
        request.open('POST', `${this.baseUrl}/documents`);
        request.setRequestHeader('Accept', 'application/json');
        request.setRequestHeader('Authorization', `Bearer ${token}`);

        request.upload.onprogress = (event) => {
          if (event.lengthComputable && event.total > 0) {
            onProgress(Math.min(100, Math.round((event.loaded / event.total) * 100)));
          }
        };

        request.onload = () => {
          if (request.status >= 200 && request.status < 300) {
            try {
              resolve(JSON.parse(request.responseText) as UploadedDocument);
            } catch {
              reject(new ApiError(request.status, 'invalid_response', 'El API devolvió una respuesta ilegible.'));
            }

            return;
          }

          const problem = parseProblemJson(request.responseText);
          const code = typeof problem?.code === 'string' ? problem.code : `http_${request.status}`;
          const detail =
            typeof problem?.detail === 'string' ? problem.detail : 'El archivo no pudo subirse.';
          const correlationId =
            typeof problem?.correlationId === 'string' ? problem.correlationId : undefined;

          reject(new ApiError(request.status, code, detail, correlationId));
        };

        request.onerror = () =>
          reject(new ApiError(0, 'network_error', 'No se pudo contactar el API.'));
        request.onabort = () =>
          reject(new ApiError(0, 'upload_aborted', 'La subida fue cancelada.'));

        const body = new FormData();
        body.append('file', file);
        request.send(body);
      });
    });
  }

  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const token = await this.auth.getAccessToken();

    if (!token) {
      throw new ApiError(401, 'unauthenticated', 'No hay una sesión activa.');
    }

    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json');
    headers.set('Authorization', `Bearer ${token}`);

    if (init.body !== undefined) {
      headers.set('Content-Type', 'application/json');
    }

    let response: Response;

    try {
      response = await fetch(`${this.baseUrl}${path}`, { ...init, headers });
    } catch {
      throw new ApiError(0, 'network_error', 'No se pudo contactar el API.');
    }

    if (!response.ok) {
      throw await readProblem(response);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }
}
