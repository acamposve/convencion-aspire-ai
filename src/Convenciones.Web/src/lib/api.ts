import type { AuthClient } from './auth';
import { ApiError } from './errors';

export const MAX_DOCUMENT_BYTES = 20 * 1024 * 1024;

export interface Page<TItem> {
  items: TItem[];
  page: number;
  pageSize: number;
  total: number;
}

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

// ---- Documents (T4–T7) ---------------------------------------------------------------------------

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

// ---- Clause processing (T44/T45) -----------------------------------------------------------------

export type ProcessingStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

export type ClauseReviewStatus = 'Pending' | 'Accepted' | 'Corrected' | 'Rejected';

export interface ProcessingRun {
  id: string;
  documentId: string;
  status: ProcessingStatus;
  stage: string;
  attempts: number;
  clauseCount: number;
  classifiedCount: number;
  unclassifiedCount: number;
  errorCode: string | null;
  errorMessage: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface ClauseClassificationView {
  key: string;
  name: string;
  confidence: number | null;
  isPrimary: boolean;
  rationale: string | null;
  source: 'ai' | 'human';
}

export interface ProcessingClause {
  id: string;
  ordinal: number;
  number: string | null;
  heading: string | null;
  text: string;
  pageFrom: number;
  pageTo: number;
  sourceCharStart: number | null;
  sourceCharEnd: number | null;
  confidence: number | null;
  needsReview: boolean;
  reviewStatus: ClauseReviewStatus;
  classifications: ClauseClassificationView[];
  classificationVersion: string;
}

export interface ClausePageResponse {
  items: ProcessingClause[];
  page: number;
  pageSize: number;
  total: number;
}

export interface ClauseCategoryView {
  key: string;
  name: string;
  description: string | null;
  parentId: string | null;
  sortOrder: number;
}
// ---- Agreements and clauses (T8) -----------------------------------------------------------------

export type PublicationState = 'Draft' | 'Published';

export interface AgreementSummary {
  id: string;
  companyId: string;
  companyName: string;
  title: string;
  validFrom: string;
  validTo: string | null;
  scope: string;
  state: PublicationState;
  publishedAt: string | null;
  clauseCount: number;
  rowVersion: number;
}

export interface ClauseView {
  id: string;
  number: number;
  title: string;
  summary: string | null;
  body: string;
  headingId: string;
  headingName: string | null;
  economicComparisonNote: string | null;
  isExcludedFromCompilation: boolean;
  exclusionNote: string | null;
  rowVersion: number;
}

export interface AnnexView {
  id: string;
  name: string;
  description: string | null;
  body: string | null;
  documentId: string | null;
  sortOrder: number;
  rowVersion: number;
}

export interface AgreementDetail extends Omit<AgreementSummary, 'clauseCount'> {
  createdAt: string;
  updatedAt: string;
  clauses: ClauseView[];
  annexes: AnnexView[];
}

export interface AgreementListResponse extends Page<AgreementSummary> {}

export interface CreateAgreementInput {
  companyId?: string | null;
  validFrom: string;
  validTo?: string | null;
  scope: string;
}

export interface UpdateAgreementInput {
  validFrom: string;
  validTo?: string | null;
  scope: string;
}

export interface ClauseInput {
  number: number;
  title: string;
  summary?: string | null;
  body: string;
  headingId: string;
  economicComparisonNote?: string | null;
  isExcludedFromCompilation?: boolean;
  exclusionNote?: string | null;
}

// ---- Comparison (T9) ----------------------------------------------------------------------------

export interface HeadingGroupView {
  id: string;
  name: string;
  description: string | null;
  requiresEconomicComparison: boolean;
  sortOrder: number;
}

export interface HeadingView {
  id: string;
  groupId: string;
  groupName: string;
  /**
   * The group's definition, shown next to the materia (#138a). `listHeadings` always fills it;
   * `ComparisonView.heading` comes from the comparison projection, which does not carry it, so the
   * screen reads the definition from the heading it loaded.
   */
  groupDescription?: string | null;
  name: string;
  isExcluded: boolean;
  requiresEconomicComparison: boolean;
  sortOrder: number;
}

/**
 * The wire shape of `GET /comparison/headings`: heading **groups** with their headings nested, ordered by
 * the API. Only `listHeadings` consumes it; every screen receives the flattened `HeadingView[]`.
 */
export interface HeadingGroupWithHeadings {
  id: string;
  name: string;
  description: string | null;
  requiresEconomicComparison: boolean;
  headings?: WireHeading[];
}

export interface WireHeading {
  id: string;
  groupId: string;
  name: string;
  description: string | null;
  isExcluded: boolean;
  sortOrder: number;
}

export interface ComparisonClauseView {
  clauseId: string;
  number: number;
  title: string;
  summary: string | null;
  body: string;
  economicComparisonNote: string | null;
  isExcludedFromCompilation: boolean;
}

export interface ComparisonAgreementView {
  agreementId: string;
  companyId: string;
  companyName: string;
  title: string;
  validFrom: string;
  validTo: string | null;
  clauses: ComparisonClauseView[];
}

export interface ComparisonStatuteArticleView {
  statuteId: string;
  statuteName: string;
  statuteKind: string;
  articleId: string;
  number: number;
  title: string;
  body: string;
  economicComparisonNote: string | null;
}

export interface ComparisonView {
  heading: HeadingView;
  statuteArticles: ComparisonStatuteArticleView[];
  statuteArticleTotal: number;
  agreements: ComparisonAgreementView[];
  unpublishedRequested: string[];
  page: number;
  pageSize: number;
  total: number;
}

export interface ComparisonCandidate {
  agreementId: string;
  companyId: string;
  companyName: string;
  title: string;
  validFrom: string;
  validTo: string | null;
  clauseCount: number;
}

// ---- Legal library (T10) ------------------------------------------------------------------------

export type StatuteKind = 'LabourLaw' | 'OtherStatute';

export interface StatuteSummary {
  id: string;
  kind: StatuteKind;
  name: string;
  publishedOn: string | null;
  source: string | null;
  documentId: string | null;
  articleCount: number;
  rowVersion: number;
}

export interface StatuteArticleView {
  id: string;
  number: number;
  title: string;
  summary: string | null;
  body: string;
  headingId: string | null;
  economicComparisonNote: string | null;
  rowVersion: number;
}

export interface StatuteDetail {
  id: string;
  kind: StatuteKind;
  name: string;
  publishedOn: string | null;
  source: string | null;
  documentId: string | null;
  createdAt: string;
  updatedAt: string;
  rowVersion: number;
  articles: StatuteArticleView[];
}

export interface StatuteArticleInput {
  number: number;
  title: string;
  summary?: string | null;
  body: string;
  headingId?: string | null;
  economicComparisonNote?: string | null;
}

// ---- Negotiation (T11) --------------------------------------------------------------------------

export type CaseState = 'Open' | 'InProgress' | 'Pending' | 'Agreed' | 'Closed';

export type CaseTransition = 'in-progress' | 'pending' | 'agree' | 'close' | 'reopen';

export interface PartyView {
  id: string;
  role: string;
  name: string;
  phone: string | null;
  email: string | null;
  position: string | null;
}

export interface OfferView {
  id: string;
  number: number;
  title: string;
  body: string;
}

export interface MeetingAgreementView {
  id: string;
  petitionId: string;
  meetingId: string;
  agreedOn: string;
  number: number;
  title: string;
  body: string;
  headingId: string | null;
}

export interface PetitionView {
  id: string;
  number: number;
  title: string;
  body: string;
  headingId: string | null;
  state: string;
  offers: OfferView[];
  agreements: MeetingAgreementView[];
}

export interface MeetingView {
  id: string;
  heldOn: string;
  startTime: string | null;
  duration: string | null;
  summary: string | null;
  details: string | null;
  attendees: string | null;
}

export interface NegotiationCaseSummary {
  id: string;
  companyId: string;
  startedOn: string;
  state: CaseState;
  closedAt: string | null;
  promotedAgreementId: string | null;
  partyCount: number;
  petitionCount: number;
  meetingCount: number;
  meetingAgreementCount: number;
}

export interface NegotiationCaseDetail {
  id: string;
  companyId: string;
  startedOn: string;
  state: CaseState;
  closedAt: string | null;
  promotedAgreementId: string | null;
  createdAt: string;
  updatedAt: string;
  parties: PartyView[];
  petitions: PetitionView[];
  meetings: MeetingView[];
}

export interface PromoteCaseInput {
  validFrom: string;
  validTo?: string | null;
  scope: string;
}

// ---- News (T12) ---------------------------------------------------------------------------------

export interface NewsSummary {
  id: string;
  title: string;
  summary: string | null;
  categoryId: string;
  categoryName: string;
  state: PublicationState;
  publishedAt: string | null;
  updatedAt: string;
}

export interface NewsDetail {
  id: string;
  categoryId: string;
  categoryName: string;
  title: string;
  summary: string | null;
  body: string;
  state: PublicationState;
  publishedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface NewsCategoryView {
  id: string;
  name: string;
  description: string | null;
  sortOrder: number;
  isActive: boolean;
}

// ---- Administration and audit (T13) -------------------------------------------------------------

export interface UserSummary {
  id: string;
  email: string;
  displayName: string;
  isBlocked: boolean;
  expiresOn: string | null;
  companyId: string | null;
  companyName: string | null;
  roles: string[];
  createdAt: string;
  updatedAt: string;
}

export interface UserDetail extends UserSummary {
  phone: string | null;
}

export interface RoleView {
  id: string;
  key: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  capabilities: string[];
}

export interface CapabilityView {
  key: string;
  description: string;
}

export interface AuditEntryView {
  id: number;
  occurredAt: string;
  actorId: string | null;
  actorDisplayName: string | null;
  module: string;
  entityType: string;
  entityId: string;
  action: string;
  fromState: string | null;
  toState: string | null;
  changes: unknown;
}

// ---- Public portal (T12) ------------------------------------------------------------------------

export interface PublicClause {
  number: number;
  title: string;
  summary: string | null;
  body: string;
}

export interface PublicAnnex {
  name: string;
  description: string | null;
  body: string | null;
}

export interface PublicAgreementSummary {
  id: string;
  title: string;
  companyName: string;
  validFrom: string;
  validTo: string | null;
  scope: string;
  publishedAt: string;
}

export interface PublicAgreementDetail extends PublicAgreementSummary {
  clauses: PublicClause[];
  annexes: PublicAnnex[];
}

export interface PublicNewsSummary {
  id: string;
  title: string;
  summary: string | null;
  categoryName: string;
  publishedAt: string;
}

export interface PublicNewsDetail extends PublicNewsSummary {
  body: string;
}

// ---- Clients ------------------------------------------------------------------------------------

export interface ApiClient {
  getMe(): Promise<Me>;
  getCompany(id: string): Promise<Company>;

  listDocuments(page: number, pageSize: number): Promise<DocumentListResponse>;
  uploadDocument(file: File, onProgress: (percent: number) => void): Promise<UploadedDocument>;
  extractDocument(id: string): Promise<ExtractionState>;
  getDocument(id: string): Promise<DocumentDetail>;
  getDocumentText(id: string): Promise<DocumentText>;

  startProcessing(documentId: string): Promise<ProcessingRun>;
  getProcessing(documentId: string): Promise<ProcessingRun>;
  listClauses(documentId: string, page: number, pageSize: number): Promise<ClausePageResponse>;
  reviewClause(
    clauseId: string,
    status: ClauseReviewStatus,
    input?: {
      text?: string;
      heading?: string;
      classifications?: Array<{ key: string; confidence?: number; rationale?: string | null }>;
      primary?: string | null;
      expectedVersion?: string | null;
    },
  ): Promise<ProcessingClause>;
  listClauseCategories(): Promise<ClauseCategoryView[]>;

  listAgreements(query: {
    page: number;
    pageSize: number;
    state?: PublicationState | null;
    q?: string | null;
    companyId?: string | null;
  }): Promise<AgreementListResponse>;
  getAgreement(id: string): Promise<AgreementDetail>;
  createAgreement(input: CreateAgreementInput): Promise<AgreementDetail>;
  updateAgreement(
    id: string,
    input: UpdateAgreementInput,
    rowVersion: number,
  ): Promise<AgreementDetail>;
  publishAgreement(id: string): Promise<AgreementDetail>;
  unpublishAgreement(id: string): Promise<AgreementDetail>;
  createClause(agreementId: string, input: ClauseInput): Promise<ClauseView>;
  updateClause(
    agreementId: string,
    clauseId: string,
    input: Partial<ClauseInput>,
    rowVersion: number,
  ): Promise<ClauseView>;
  deleteClause(agreementId: string, clauseId: string, rowVersion: number): Promise<void>;

  listHeadingGroups(): Promise<HeadingGroupView[]>;
  listHeadings(includeExcluded?: boolean): Promise<HeadingView[]>;
  listCandidates(query: {
    headingId?: string | null;
    q?: string | null;
    page: number;
    pageSize: number;
  }): Promise<Page<ComparisonCandidate>>;
  compare(query: {
    headingId: string;
    agreementIds: string[];
    page: number;
    pageSize: number;
  }): Promise<ComparisonView>;

  listStatutes(page: number, pageSize: number): Promise<Page<StatuteSummary>>;
  getStatute(id: string): Promise<StatuteDetail>;
  createStatuteArticle(statuteId: string, input: StatuteArticleInput): Promise<StatuteArticleView>;
  updateStatuteArticle(
    statuteId: string,
    articleId: string,
    input: StatuteArticleInput,
    rowVersion: number,
  ): Promise<StatuteArticleView>;

  listCases(query: {
    page: number;
    pageSize: number;
    state?: CaseState | null;
  }): Promise<Page<NegotiationCaseSummary>>;
  getCase(id: string): Promise<NegotiationCaseDetail>;
  createCase(startedOn: string): Promise<NegotiationCaseDetail>;
  transitionCase(id: string, step: CaseTransition): Promise<NegotiationCaseDetail>;
  promoteCase(id: string, input: PromoteCaseInput): Promise<{ agreementId: string; caseId: string }>;
  addPetition(
    caseId: string,
    input: { number: number; title: string; body: string; headingId?: string | null },
  ): Promise<PetitionView>;
  addMeeting(
    caseId: string,
    input: { heldOn: string; summary?: string | null; attendees?: string | null },
  ): Promise<MeetingView>;
  addMeetingAgreement(
    caseId: string,
    petitionId: string,
    input: { meetingId: string; agreedOn: string; number: number; title: string; body: string },
  ): Promise<MeetingAgreementView>;

  listNews(query: {
    page: number;
    pageSize: number;
    state?: PublicationState | null;
  }): Promise<Page<NewsSummary>>;
  createNews(input: {
    categoryId: string;
    title: string;
    summary?: string | null;
    body: string;
  }): Promise<NewsDetail>;
  publishNews(id: string): Promise<NewsDetail>;
  unpublishNews(id: string): Promise<NewsDetail>;
  listNewsCategories(): Promise<NewsCategoryView[]>;

  listUsers(query: {
    page: number;
    pageSize: number;
    q?: string | null;
  }): Promise<Page<UserSummary>>;
  getUser(id: string): Promise<UserDetail>;
  createUser(input: {
    subject: string;
    email: string;
    displayName: string;
    phone?: string | null;
    companyId?: string | null;
    roleKeys: string[];
  }): Promise<UserDetail>;
  updateUser(
    id: string,
    input: {
      displayName?: string;
      email?: string;
      phone?: string | null;
      companyId?: string | null;
      isBlocked?: boolean;
      expiresOn?: string | null;
    },
  ): Promise<UserDetail>;
  replaceUserRoles(id: string, roleKeys: string[]): Promise<UserDetail>;
  listRoles(): Promise<RoleView[]>;
  listCapabilities(): Promise<CapabilityView[]>;
  listAudit(query: {
    page: number;
    pageSize: number;
    module?: string | null;
    entityType?: string | null;
    entityId?: string | null;
    actorId?: string | null;
    from?: string | null;
    to?: string | null;
  }): Promise<Page<AuditEntryView>>;
}

/** The anonymous surface. It sends no Authorization header, by construction. */
export interface PublicApi {
  listAgreements(page: number, pageSize: number): Promise<Page<PublicAgreementSummary>>;
  getAgreement(id: string): Promise<PublicAgreementDetail>;
  listNews(page: number, pageSize: number): Promise<Page<PublicNewsSummary>>;
  getNews(id: string): Promise<PublicNewsDetail>;
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

function queryString(values: Record<string, string | number | null | undefined>): string {
  const params = new URLSearchParams();

  for (const [key, value] of Object.entries(values)) {
    if (value !== null && value !== undefined && value !== '') {
      params.set(key, String(value));
    }
  }

  const query = params.toString();
  return query === '' ? '' : `?${query}`;
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
    return this.request<DocumentListResponse>(`/documents${queryString({ page, pageSize })}`);
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

  async startProcessing(documentId: string): Promise<ProcessingRun> {
    return this.request<ProcessingRun>(`/documents/${encodeURIComponent(documentId)}/process`, {
      method: 'POST',
    });
  }

  async getProcessing(documentId: string): Promise<ProcessingRun> {
    return this.request<ProcessingRun>(`/documents/${encodeURIComponent(documentId)}/processing`);
  }

  async listClauses(documentId: string, page: number, pageSize: number): Promise<ClausePageResponse> {
    return this.request<ClausePageResponse>(
      `/documents/${encodeURIComponent(documentId)}/clauses${queryString({ page, pageSize })}`,
    );
  }

  async reviewClause(
    clauseId: string,
    status: ClauseReviewStatus,
    input: {
      text?: string;
      heading?: string;
      classifications?: Array<{ key: string; confidence?: number; rationale?: string | null }>;
      primary?: string | null;
      expectedVersion?: string | null;
    } = {},
  ): Promise<ProcessingClause> {
    return this.request<ProcessingClause>(`/clauses/${encodeURIComponent(clauseId)}`, {
      method: 'PATCH',
      body: JSON.stringify({
        status,
        text: input.text ?? null,
        heading: input.heading ?? null,
        classifications: input.classifications ?? null,
        primary: input.primary ?? null,
        expectedVersion: input.expectedVersion ?? null,
      }),
    });
  }

  async listClauseCategories(): Promise<ClauseCategoryView[]> {
    return this.request<ClauseCategoryView[]>('/clause-categories');
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
              reject(
                new ApiError(
                  request.status,
                  'invalid_response',
                  'El API devolvió una respuesta ilegible.',
                ),
              );
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

  async listAgreements(query: {
    page: number;
    pageSize: number;
    state?: PublicationState | null;
    q?: string | null;
    companyId?: string | null;
  }): Promise<AgreementListResponse> {
    return this.request<AgreementListResponse>(`/agreements${queryString(query)}`);
  }

  async getAgreement(id: string): Promise<AgreementDetail> {
    return this.request<AgreementDetail>(`/agreements/${encodeURIComponent(id)}`);
  }

  async createAgreement(input: CreateAgreementInput): Promise<AgreementDetail> {
    return this.request<AgreementDetail>('/agreements', {
      method: 'POST',
      body: JSON.stringify(input),
    });
  }

  async updateAgreement(
    id: string,
    input: UpdateAgreementInput,
    rowVersion: number,
  ): Promise<AgreementDetail> {
    return this.request<AgreementDetail>(`/agreements/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(input),
      headers: { 'If-Match': String(rowVersion) },
    });
  }

  async publishAgreement(id: string): Promise<AgreementDetail> {
    return this.request<AgreementDetail>(`/agreements/${encodeURIComponent(id)}/publish`, {
      method: 'POST',
    });
  }

  async unpublishAgreement(id: string): Promise<AgreementDetail> {
    return this.request<AgreementDetail>(`/agreements/${encodeURIComponent(id)}/unpublish`, {
      method: 'POST',
    });
  }

  async createClause(agreementId: string, input: ClauseInput): Promise<ClauseView> {
    return this.request<ClauseView>(`/agreements/${encodeURIComponent(agreementId)}/clauses`, {
      method: 'POST',
      body: JSON.stringify(input),
    });
  }

  async updateClause(
    agreementId: string,
    clauseId: string,
    input: Partial<ClauseInput>,
    rowVersion: number,
  ): Promise<ClauseView> {
    return this.request<ClauseView>(
      `/agreements/${encodeURIComponent(agreementId)}/clauses/${encodeURIComponent(clauseId)}`,
      {
        method: 'PATCH',
        body: JSON.stringify(input),
        headers: { 'If-Match': String(rowVersion) },
      },
    );
  }

  async deleteClause(agreementId: string, clauseId: string, rowVersion: number): Promise<void> {
    return this.request<void>(
      `/agreements/${encodeURIComponent(agreementId)}/clauses/${encodeURIComponent(clauseId)}`,
      { method: 'DELETE', headers: { 'If-Match': String(rowVersion) } },
    );
  }

  async listHeadingGroups(): Promise<HeadingGroupView[]> {
    return this.request<HeadingGroupView[]>('/comparison/heading-groups');
  }

  /**
   * One flat list of headings for the screens that need a single selector (comparison, clause editor,
   * statute article).
   *
   * `GET /comparison/headings` answers with groups that carry their headings nested, because the
   * comparison workspace needs the group name and its definition. Flattening here keeps that wire shape in
   * one place: sending a group id where a heading id is expected is exactly the defect #135 fixed, and it
   * would come back if every screen did its own mapping.
   *
   * Order is explicit and stable: group position as returned by the API (the API sorts groups by their
   * `sortOrder`), then the heading's `sortOrder`, then its name. A group without headings is skipped
   * instead of throwing.
   */
  async listHeadings(includeExcluded = true): Promise<HeadingView[]> {
    const query = includeExcluded ? '?includeExcluded=true' : '';
    const groups = await this.request<HeadingGroupWithHeadings[]>(`/comparison/headings${query}`);

    const flattened = groups.flatMap((group, groupPosition) =>
      (group.headings ?? []).map((heading) => ({ group, groupPosition, heading })),
    );

    return flattened
      .sort(
        (left, right) =>
          left.groupPosition - right.groupPosition ||
          left.heading.sortOrder - right.heading.sortOrder ||
          left.heading.name.localeCompare(right.heading.name),
      )
      .map(({ group, heading }) => ({
        id: heading.id,
        groupId: group.id,
        groupName: group.name,
        groupDescription: group.description,
        name: heading.name,
        isExcluded: heading.isExcluded,
        requiresEconomicComparison: group.requiresEconomicComparison,
        sortOrder: heading.sortOrder,
      }));
  }

  async listCandidates(query: {
    headingId?: string | null;
    q?: string | null;
    page: number;
    pageSize: number;
  }): Promise<Page<ComparisonCandidate>> {
    return this.request<Page<ComparisonCandidate>>(`/comparison/candidates${queryString(query)}`);
  }

  async compare(query: {
    headingId: string;
    agreementIds: string[];
    page: number;
    pageSize: number;
  }): Promise<ComparisonView> {
    return this.request<ComparisonView>(
      `/comparison${queryString({
        headingId: query.headingId,
        agreementIds: query.agreementIds.join(','),
        page: query.page,
        pageSize: query.pageSize,
      })}`,
    );
  }

  async listStatutes(page: number, pageSize: number): Promise<Page<StatuteSummary>> {
    return this.request<Page<StatuteSummary>>(`/statutes${queryString({ page, pageSize })}`);
  }

  async getStatute(id: string): Promise<StatuteDetail> {
    return this.request<StatuteDetail>(`/statutes/${encodeURIComponent(id)}`);
  }

  async createStatuteArticle(
    statuteId: string,
    input: StatuteArticleInput,
  ): Promise<StatuteArticleView> {
    return this.request<StatuteArticleView>(
      `/statutes/${encodeURIComponent(statuteId)}/articles`,
      { method: 'POST', body: JSON.stringify(input) },
    );
  }

  async updateStatuteArticle(
    statuteId: string,
    articleId: string,
    input: StatuteArticleInput,
    rowVersion: number,
  ): Promise<StatuteArticleView> {
    return this.request<StatuteArticleView>(
      `/statutes/${encodeURIComponent(statuteId)}/articles/${encodeURIComponent(articleId)}`,
      {
        method: 'PATCH',
        body: JSON.stringify(input),
        headers: { 'If-Match': String(rowVersion) },
      },
    );
  }

  async listCases(query: {
    page: number;
    pageSize: number;
    state?: CaseState | null;
  }): Promise<Page<NegotiationCaseSummary>> {
    return this.request<Page<NegotiationCaseSummary>>(
      `/negotiation/cases${queryString(query)}`,
    );
  }

  async getCase(id: string): Promise<NegotiationCaseDetail> {
    return this.request<NegotiationCaseDetail>(`/negotiation/cases/${encodeURIComponent(id)}`);
  }

  async createCase(startedOn: string): Promise<NegotiationCaseDetail> {
    return this.request<NegotiationCaseDetail>('/negotiation/cases', {
      method: 'POST',
      body: JSON.stringify({ startedOn }),
    });
  }

  async transitionCase(id: string, step: CaseTransition): Promise<NegotiationCaseDetail> {
    return this.request<NegotiationCaseDetail>(
      `/negotiation/cases/${encodeURIComponent(id)}/${step}`,
      { method: 'POST' },
    );
  }

  async promoteCase(
    id: string,
    input: PromoteCaseInput,
  ): Promise<{ agreementId: string; caseId: string }> {
    return this.request<{ agreementId: string; caseId: string }>(
      `/negotiation/cases/${encodeURIComponent(id)}/promote`,
      { method: 'POST', body: JSON.stringify(input) },
    );
  }

  async addPetition(
    caseId: string,
    input: { number: number; title: string; body: string; headingId?: string | null },
  ): Promise<PetitionView> {
    return this.request<PetitionView>(
      `/negotiation/cases/${encodeURIComponent(caseId)}/petitions`,
      { method: 'POST', body: JSON.stringify(input) },
    );
  }

  async addMeeting(
    caseId: string,
    input: { heldOn: string; summary?: string | null; attendees?: string | null },
  ): Promise<MeetingView> {
    return this.request<MeetingView>(
      `/negotiation/cases/${encodeURIComponent(caseId)}/meetings`,
      { method: 'POST', body: JSON.stringify(input) },
    );
  }

  async addMeetingAgreement(
    caseId: string,
    petitionId: string,
    input: { meetingId: string; agreedOn: string; number: number; title: string; body: string },
  ): Promise<MeetingAgreementView> {
    return this.request<MeetingAgreementView>(
      `/negotiation/cases/${encodeURIComponent(caseId)}/petitions/${encodeURIComponent(petitionId)}/agreements`,
      { method: 'POST', body: JSON.stringify(input) },
    );
  }

  async listNews(query: {
    page: number;
    pageSize: number;
    state?: PublicationState | null;
  }): Promise<Page<NewsSummary>> {
    return this.request<Page<NewsSummary>>(`/news${queryString(query)}`);
  }

  async createNews(input: {
    categoryId: string;
    title: string;
    summary?: string | null;
    body: string;
  }): Promise<NewsDetail> {
    return this.request<NewsDetail>('/news', { method: 'POST', body: JSON.stringify(input) });
  }

  async publishNews(id: string): Promise<NewsDetail> {
    return this.request<NewsDetail>(`/news/${encodeURIComponent(id)}/publish`, { method: 'POST' });
  }

  async unpublishNews(id: string): Promise<NewsDetail> {
    return this.request<NewsDetail>(`/news/${encodeURIComponent(id)}/unpublish`, {
      method: 'POST',
    });
  }

  async listNewsCategories(): Promise<NewsCategoryView[]> {
    return this.request<NewsCategoryView[]>('/news/categories');
  }

  async listUsers(query: {
    page: number;
    pageSize: number;
    q?: string | null;
  }): Promise<Page<UserSummary>> {
    return this.request<Page<UserSummary>>(`/users${queryString(query)}`);
  }

  async getUser(id: string): Promise<UserDetail> {
    return this.request<UserDetail>(`/users/${encodeURIComponent(id)}`);
  }

  async createUser(input: {
    subject: string;
    email: string;
    displayName: string;
    phone?: string | null;
    companyId?: string | null;
    roleKeys: string[];
  }): Promise<UserDetail> {
    return this.request<UserDetail>('/users', { method: 'POST', body: JSON.stringify(input) });
  }

  async updateUser(
    id: string,
    input: {
      displayName?: string;
      email?: string;
      phone?: string | null;
      companyId?: string | null;
      isBlocked?: boolean;
      expiresOn?: string | null;
    },
  ): Promise<UserDetail> {
    return this.request<UserDetail>(`/users/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(input),
    });
  }

  async replaceUserRoles(id: string, roleKeys: string[]): Promise<UserDetail> {
    return this.request<UserDetail>(`/users/${encodeURIComponent(id)}/roles`, {
      method: 'PUT',
      body: JSON.stringify({ roleKeys }),
    });
  }

  async listRoles(): Promise<RoleView[]> {
    return this.request<RoleView[]>('/roles');
  }

  async listCapabilities(): Promise<CapabilityView[]> {
    return this.request<CapabilityView[]>('/capabilities');
  }

  async listAudit(query: {
    page: number;
    pageSize: number;
    module?: string | null;
    entityType?: string | null;
    entityId?: string | null;
    actorId?: string | null;
    from?: string | null;
    to?: string | null;
  }): Promise<Page<AuditEntryView>> {
    return this.request<Page<AuditEntryView>>(`/audit${queryString(query)}`);
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

/**
 * The anonymous client used by the public portal.
 *
 * It holds no token and sends no Authorization header — there is nothing to send. That is the
 * client-side half of AC 4; the server half is that `/public/*` runs on the `anon` database role.
 */
export class HttpPublicApi implements PublicApi {
  private readonly baseUrl: string;

  constructor(baseUrl: string) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
  }

  async listAgreements(page: number, pageSize: number): Promise<Page<PublicAgreementSummary>> {
    return this.request<Page<PublicAgreementSummary>>(
      `/public/agreements${queryString({ page, pageSize })}`,
    );
  }

  async getAgreement(id: string): Promise<PublicAgreementDetail> {
    return this.request<PublicAgreementDetail>(`/public/agreements/${encodeURIComponent(id)}`);
  }

  async listNews(page: number, pageSize: number): Promise<Page<PublicNewsSummary>> {
    return this.request<Page<PublicNewsSummary>>(`/public/news${queryString({ page, pageSize })}`);
  }

  async getNews(id: string): Promise<PublicNewsDetail> {
    return this.request<PublicNewsDetail>(`/public/news/${encodeURIComponent(id)}`);
  }

  private async request<T>(path: string): Promise<T> {
    let response: Response;

    try {
      response = await fetch(`${this.baseUrl}${path}`, {
        headers: { Accept: 'application/json' },
      });
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
