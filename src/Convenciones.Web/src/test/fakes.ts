import type { AuthClient } from '../lib/auth';
import { AuthError, ApiError } from '../lib/errors';
import type {
  AgreementDetail,
  AgreementSummary,
  ApiClient,
  AuditEntryView,
  CapabilityView,
  ClauseCategoryView,
  ClausePageResponse,
  ClauseReviewStatus,
  ClauseView,
  ComparisonCandidate,
  ComparisonView,
  DocumentListResponse,
  DocumentSummary,
  DocumentText,
  ExtractionState,
  HeadingView,
  Me,
  MeetingAgreementView,
  MeetingView,
  NegotiationCaseDetail,
  NewsCategoryView,
  NewsDetail,
  NewsSummary,
  Page,
  PetitionView,
  ProcessingClause,
  ProcessingRun,
  PublicAgreementDetail,
  PublicAgreementSummary,
  PublicApi,
  PublicNewsDetail,
  PublicNewsSummary,
  RoleView,
  StatuteArticleView,
  StatuteDetail,
  UserDetail,
  UserSummary,
  UploadedDocument,
  Company,
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

// ---- Factories for the T14 screens ---------------------------------------------------------------

let sequence = 0;

function nextId(prefix: string): string {
  sequence += 1;
  return `${prefix}-${sequence}`;
}

export function makeHeading(overrides: Partial<HeadingView> = {}): HeadingView {
  return {
    id: nextId('heading'),
    groupId: 'group-1',
    groupName: 'GENERALES',
    name: 'Salarios',
    isExcluded: false,
    requiresEconomicComparison: true,
    sortOrder: 1,
    ...overrides,
  };
}

export function makeClause(overrides: Partial<ClauseView> = {}): ClauseView {
  return {
    id: nextId('clause'),
    number: 1,
    title: 'Salario básico',
    summary: null,
    body: '<p>Texto de la cláusula.</p>',
    headingId: 'heading-1',
    headingName: 'Salarios',
    economicComparisonNote: null,
    isExcludedFromCompilation: false,
    exclusionNote: null,
    rowVersion: 1,
    ...overrides,
  };
}

export function makeAgreementDetail(overrides: Partial<AgreementDetail> = {}): AgreementDetail {
  const id = overrides.id ?? nextId('agreement');

  return {
    id,
    companyId: 'company-1',
    companyName: 'Empresa Demo',
    title: 'Empresa Demo · 2026',
    validFrom: '2026-01-01',
    validTo: '2028-12-31',
    scope: 'Nacional',
    state: 'Draft',
    publishedAt: null,
    rowVersion: 1,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    clauses: [],
    annexes: [],
    ...overrides,
  };
}

export function asSummary(agreement: AgreementDetail): AgreementSummary {
  return {
    id: agreement.id,
    companyId: agreement.companyId,
    companyName: agreement.companyName,
    title: agreement.title,
    validFrom: agreement.validFrom,
    validTo: agreement.validTo,
    scope: agreement.scope,
    state: agreement.state,
    publishedAt: agreement.publishedAt,
    clauseCount: agreement.clauses.length,
    rowVersion: agreement.rowVersion,
  };
}

export function makeStatute(overrides: Partial<StatuteDetail> = {}): StatuteDetail {
  return {
    id: nextId('statute'),
    kind: 'LabourLaw',
    name: 'Ley Orgánica del Trabajo',
    publishedOn: '2012-05-07',
    source: 'Gaceta Oficial',
    documentId: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    rowVersion: 1,
    articles: [],
    ...overrides,
  };
}

export function makeArticle(overrides: Partial<StatuteArticleView> = {}): StatuteArticleView {
  return {
    id: nextId('article'),
    number: 1,
    title: 'Objeto',
    summary: null,
    body: 'Texto del artículo.',
    headingId: null,
    economicComparisonNote: null,
    rowVersion: 1,
    ...overrides,
  };
}

export function makeCase(overrides: Partial<NegotiationCaseDetail> = {}): NegotiationCaseDetail {
  return {
    id: nextId('case'),
    companyId: 'company-1',
    startedOn: '2026-01-01',
    state: 'Open',
    closedAt: null,
    promotedAgreementId: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    parties: [],
    petitions: [],
    meetings: [],
    ...overrides,
  };
}

export function makePetition(overrides: Partial<PetitionView> = {}): PetitionView {
  return {
    id: nextId('petition'),
    number: 1,
    title: 'Aumento salarial',
    body: 'Petición del sindicato.',
    headingId: null,
    state: 'Open',
    offers: [],
    agreements: [],
    ...overrides,
  };
}

export function makeNewsDetail(overrides: Partial<NewsDetail> = {}): NewsDetail {
  return {
    id: nextId('news'),
    categoryId: 'category-1',
    categoryName: 'Noticias',
    title: 'Nueva convención',
    summary: 'Resumen',
    body: '<p>Cuerpo de la noticia.</p>',
    state: 'Draft',
    publishedAt: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

export function makeUser(overrides: Partial<UserDetail> = {}): UserDetail {
  return {
    id: nextId('user'),
    email: 'persona@example.test',
    displayName: 'Persona',
    isBlocked: false,
    expiresOn: null,
    companyId: 'company-1',
    companyName: 'Empresa Demo',
    roles: ['editor'],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    phone: null,
    ...overrides,
  };
}

export function makeRole(overrides: Partial<RoleView> = {}): RoleView {
  return {
    id: nextId('role'),
    key: 'editor',
    name: 'Editor',
    description: null,
    isSystem: true,
    capabilities: ['agreement.read', 'agreement.write'],
    ...overrides,
  };
}

export function makeAuditEntry(overrides: Partial<AuditEntryView> = {}): AuditEntryView {
  return {
    id: (sequence += 1),
    occurredAt: '2026-01-15T10:00:00Z',
    actorId: 'user-1',
    actorDisplayName: 'Ana Editora',
    module: 'agreements',
    entityType: 'agreement',
    entityId: 'agreement-1',
    action: 'updated',
    fromState: null,
    toState: null,
    changes: { title: { changed: true } },
    ...overrides,
  };
}

function pageOf<T>(items: T[], page: number, pageSize: number): Page<T> {
  const start = (page - 1) * pageSize;
  return { items: items.slice(start, start + pageSize), page, pageSize, total: items.length };
}

function missing(id: string): never {
  throw new ApiError(404, 'not_found', `No existe ${id}.`);
}

/** The document is visible but has no run yet: the client may start the work. */
function missingRun(id: string): never {
  throw new ApiError(404, 'run_not_found', `El documento ${id} no tiene procesamiento todavía.`);
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

/** An in-memory API with every endpoint the MVP screens use. */
export class FakeApi implements ApiClient {
  me: Me = {
    userId: 'user-1',
    email: 'ana@example.test',
    displayName: 'Ana Editora',
    companyId: 'company-1',
    capabilities: [
      'document.read',
      'document.write',
      'agreement.read',
      'agreement.write',
      'agreement.publish',
      'comparison.read',
      'statute.read',
      'statute.write',
      'negotiation.read',
      'negotiation.write',
      'negotiation.promote',
      'news.read',
      'news.write',
      'news.publish',
      'user.manage',
      'role.manage',
      'audit.read',
    ],
  };

  meError: ApiError | null = null;
  companyName = 'Empresa Demo';
  listError: ApiError | null = null;
  uploadError: ApiError | null = null;
  extractError: ApiError | null = null;
  textError: ApiError | null = null;
  extractCalls: string[] = [];
  textCalls: string[] = [];

  documents: DocumentSummary[] = [];
  texts = new Map<string, DocumentText>();
  extraction: Partial<ExtractionState> | null = null;

  agreements: AgreementDetail[] = [];
  headings: HeadingView[] = [];
  comparison: ComparisonView | null = null;
  candidates: ComparisonCandidate[] = [];
  statutes: StatuteDetail[] = [];
  cases: NegotiationCaseDetail[] = [];
  news: NewsDetail[] = [];
  newsCategories: NewsCategoryView[] = [{ id: 'category-1', name: 'Noticias', description: null, sortOrder: 1, isActive: true }];
  users: UserDetail[] = [];
  roles: RoleView[] = [makeRole({ key: 'superadmin', name: 'Superadministrador', capabilities: ['user.manage', 'role.manage', 'audit.read'] })];
  capabilities: CapabilityView[] = [{ key: 'user.manage', description: 'Administrar usuarios' }];
  audit: AuditEntryView[] = [];

  /** Errors keyed by method name, so a test can make one call fail without a stub class. */
  errors: Partial<Record<keyof ApiClient, ApiError>> = {};
  /** Names of the methods that were called, in order. */
  calls: string[] = [];

  listCalls: Array<{ page: number; pageSize: number }> = [];
  uploadCalls: File[] = [];
  uploadHandler:
    | ((file: File, onProgress: (percent: number) => void) => Promise<UploadedDocument>)
    | null = null;

  private guard(method: keyof ApiClient): void {
    this.calls.push(method);
    const error = this.errors[method];

    if (error) {
      throw error;
    }
  }

  async getMe(): Promise<Me> {
    if (this.meError) {
      throw this.meError;
    }

    return this.me;
  }

  async getCompany(id: string): Promise<Company> {
    return { id, name: this.companyName };
  }

  // ---- Documents ---------------------------------------------------------------------------------

  async listDocuments(page: number, pageSize: number): Promise<DocumentListResponse> {
    this.listCalls.push({ page, pageSize });

    if (this.listError) {
      throw this.listError;
    }

    return pageOf(this.documents, page, pageSize);
  }

  async uploadDocument(file: File, onProgress: (percent: number) => void): Promise<UploadedDocument> {
    this.uploadCalls.push(file);

    if (this.uploadHandler) {
      return this.uploadHandler(file, onProgress);
    }

    if (this.uploadError) {
      throw this.uploadError;
    }

    onProgress(100);
    const document = makeDocument({ fileName: file.name, sizeBytes: file.size });
    this.documents = [document, ...this.documents];
    return asUploadedDocument(document);
  }

  async extractDocument(id: string): Promise<ExtractionState> {
    this.extractCalls.push(id);

    if (this.extractError) {
      throw this.extractError;
    }

    const document = this.documents.find((candidate) => candidate.id === id);
    const result: ExtractionState = {
      id,
      status: 'Completed',
      attempts: (document?.extractionAttempts ?? 0) + 1,
      startedAt: '2026-01-15T10:01:00Z',
      completedAt: '2026-01-15T10:01:02Z',
      error: null,
      charCount: 42,
      ...this.extraction,
    };

    this.documents = this.documents.map((candidate) =>
      candidate.id === id
        ? {
            ...candidate,
            extractionStatus: result.status,
            extractionAttempts: result.attempts,
            extractionError: result.error,
          }
        : candidate,
    );

    return result;
  }

  async getDocument(id: string): Promise<import('../lib/api').DocumentDetail> {
    const document = this.documents.find((candidate) => candidate.id === id) ?? missing(id);
    return {
      ...document,
      extractionStartedAt: null,
      extractionCompletedAt: null,
      extractionError: document.extractionError,
      rowVersion: 1,
    };
  }

  processingRuns = new Map<string, ProcessingRun>();
  processingClauses = new Map<string, ProcessingClause[]>();
  processingCalls: string[] = [];
  processingError: ApiError | null = null;

  /** Failures for GET /documents/{id}/processing, keyed by document id. */
  processingQueryErrors = new Map<string, unknown>();

  async startProcessing(documentId: string): Promise<ProcessingRun> {
    this.processingCalls.push(documentId);
    if (this.processingError) {
      throw this.processingError;
    }

    const existing = this.processingRuns.get(documentId);
    if (existing) {
      return existing;
    }

    const run: ProcessingRun = {
      id: `run-${documentId}`,
      documentId,
      status: 'Queued',
      stage: 'Queued',
      attempts: 0,
      clauseCount: 0,
      classifiedCount: 0,
      unclassifiedCount: 0,
      errorCode: null,
      errorMessage: null,
      createdAt: '2026-01-15T10:02:00Z',
      startedAt: null,
      finishedAt: null,
    };
    this.processingRuns.set(documentId, run);
    return run;
  }

  async getProcessing(documentId: string): Promise<ProcessingRun> {
    const failure = this.processingQueryErrors.get(documentId);
    if (failure) {
      throw failure;
    }

    return this.processingRuns.get(documentId) ?? missingRun(documentId);
  }

  async listClauses(documentId: string, page: number, pageSize: number): Promise<ClausePageResponse> {
    const items = this.processingClauses.get(documentId) ?? [];
    const start = (page - 1) * pageSize;
    return { items: items.slice(start, start + pageSize), page, pageSize, total: items.length };
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
    for (const clauses of this.processingClauses.values()) {
      const clause = clauses.find((item) => item.id === clauseId);
      if (clause) {
        clause.reviewStatus = status;
        if (input.classifications) {
          const categories = this.categoryNames;
          clause.classifications = input.classifications.map((item) => ({
            key: item.key,
            name: categories.get(item.key)?.name ?? item.key,
            confidence: item.confidence ?? null,
            isPrimary: item.key === input.primary,
            rationale: item.rationale ?? null,
            source: 'human' as const,
          }));
          clause.classificationVersion = `v${Number(clause.classificationVersion.replace('v', '')) + 1}`;
        }
        return clause;
      }
    }

    return missing(clauseId);
  }

  private get categoryNames(): Map<string, ClauseCategoryView> {
    return new Map([
      [
        'pricing',
        { key: 'pricing', name: 'Precio y pago', description: null, parentId: null, sortOrder: 10 },
      ],
      [
        'scope',
        { key: 'scope', name: 'Alcance y obligaciones', description: null, parentId: null, sortOrder: 20 },
      ],
    ]);
  }

  async listClauseCategories(): Promise<ClauseCategoryView[]> {
    return [...this.categoryNames.values()];
  }

  async getDocumentText(id: string): Promise<DocumentText> {
    this.textCalls.push(id);

    if (this.textError) {
      throw this.textError;
    }

    return this.texts.get(id) ?? missing(id);
  }

  // ---- Agreements --------------------------------------------------------------------------------

  async listAgreements(query: {
    page: number;
    pageSize: number;
    state?: 'Draft' | 'Published' | null;
    q?: string | null;
  }): Promise<{ items: AgreementSummary[]; page: number; pageSize: number; total: number }> {
    this.guard('listAgreements');
    const filtered = this.agreements
      .filter((agreement) => (query.state ? agreement.state === query.state : true))
      .map(asSummary);
    return pageOf(filtered, query.page, query.pageSize);
  }

  async getAgreement(id: string): Promise<AgreementDetail> {
    this.guard('getAgreement');
    const agreement = this.agreements.find((candidate) => candidate.id === id);

    if (!agreement) {
      return missing(id);
    }

    return { ...agreement, clauses: [...agreement.clauses] };
  }

  async createAgreement(input: {
    companyId?: string | null;
    validFrom: string;
    validTo?: string | null;
    scope: string;
  }): Promise<AgreementDetail> {
    this.guard('createAgreement');
    const agreement = makeAgreementDetail({
      validFrom: input.validFrom,
      validTo: input.validTo ?? null,
      scope: input.scope,
    });
    this.agreements = [agreement, ...this.agreements];
    return agreement;
  }

  async updateAgreement(
    id: string,
    input: { validFrom: string; validTo?: string | null; scope: string },
  ): Promise<AgreementDetail> {
    this.guard('updateAgreement');
    const agreement = await this.getAgreement(id);
    const updated = {
      ...agreement,
      validFrom: input.validFrom,
      validTo: input.validTo ?? null,
      scope: input.scope,
      rowVersion: agreement.rowVersion + 1,
    };
    this.replaceAgreement(updated);
    return updated;
  }

  async publishAgreement(id: string): Promise<AgreementDetail> {
    this.guard('publishAgreement');
    const agreement = await this.getAgreement(id);

    if (agreement.clauses.length === 0) {
      throw new ApiError(409, 'invalid_state', 'Un acuerdo sin cláusulas no se puede publicar.');
    }

    const updated = {
      ...agreement,
      state: 'Published' as const,
      publishedAt: '2026-01-20T10:00:00Z',
      rowVersion: agreement.rowVersion + 1,
    };
    this.replaceAgreement(updated);
    return updated;
  }

  async unpublishAgreement(id: string): Promise<AgreementDetail> {
    this.guard('unpublishAgreement');
    const agreement = await this.getAgreement(id);
    const updated = {
      ...agreement,
      state: 'Draft' as const,
      publishedAt: null,
      rowVersion: agreement.rowVersion + 1,
    };
    this.replaceAgreement(updated);
    return updated;
  }

  async createClause(agreementId: string, input: import('../lib/api').ClauseInput): Promise<ClauseView> {
    this.guard('createClause');
    const agreement = await this.getAgreement(agreementId);
    const clause = makeClause({
      number: input.number,
      title: input.title,
      summary: input.summary ?? null,
      body: input.body,
      headingId: input.headingId,
      headingName: this.headings.find((heading) => heading.id === input.headingId)?.name ?? null,
      economicComparisonNote: input.economicComparisonNote ?? null,
      isExcludedFromCompilation: input.isExcludedFromCompilation ?? false,
      exclusionNote: input.exclusionNote ?? null,
    });
    this.replaceAgreement({ ...agreement, clauses: [...agreement.clauses, clause] });
    return clause;
  }

  async updateClause(
    agreementId: string,
    clauseId: string,
    input: Partial<import('../lib/api').ClauseInput>,
  ): Promise<ClauseView> {
    this.guard('updateClause');
    const agreement = await this.getAgreement(agreementId);
    const clause = agreement.clauses.find((candidate) => candidate.id === clauseId) ?? missing(clauseId);
    const updated = {
      ...clause,
      ...input,
      title: input.title ?? clause.title,
      body: input.body ?? clause.body,
      headingId: input.headingId ?? clause.headingId,
      rowVersion: clause.rowVersion + 1,
    };
    this.replaceAgreement({
      ...agreement,
      clauses: agreement.clauses.map((candidate) => (candidate.id === clauseId ? updated : candidate)),
    });
    return updated;
  }

  async deleteClause(agreementId: string, clauseId: string): Promise<void> {
    this.guard('deleteClause');
    const agreement = await this.getAgreement(agreementId);
    this.replaceAgreement({
      ...agreement,
      clauses: agreement.clauses.filter((candidate) => candidate.id !== clauseId),
    });
  }

  // ---- Comparison --------------------------------------------------------------------------------

  async listHeadingGroups() {
    return [];
  }

  async listHeadings(): Promise<HeadingView[]> {
    this.guard('listHeadings');
    return this.headings;
  }

  async listCandidates(_query: {
    headingId?: string | null;
    q?: string | null;
    page: number;
    pageSize: number;
  }): Promise<Page<ComparisonCandidate>> {
    this.guard('listCandidates');
    return pageOf(this.candidates, _query.page, _query.pageSize);
  }

  async compare(query: {
    headingId: string;
    agreementIds: string[];
    page: number;
    pageSize: number;
  }): Promise<ComparisonView> {
    this.guard('compare');

    if (!this.comparison) {
      return missing(query.headingId);
    }

    return this.comparison;
  }

  // ---- Legal library -----------------------------------------------------------------------------

  async listStatutes(page: number, pageSize: number): Promise<Page<import('../lib/api').StatuteSummary>> {
    this.guard('listStatutes');
    return pageOf(
      this.statutes.map((statute) => ({ ...statute, articleCount: statute.articles.length })),
      page,
      pageSize,
    );
  }

  async getStatute(id: string): Promise<StatuteDetail> {
    this.guard('getStatute');
    return this.statutes.find((statute) => statute.id === id) ?? missing(id);
  }

  async createStatuteArticle(statuteId: string, input: import('../lib/api').StatuteArticleInput) {
    this.guard('createStatuteArticle');
    const statute = await this.getStatute(statuteId);
    const article = makeArticle(input);
    this.replaceStatute({ ...statute, articles: [...statute.articles, article] });
    return article;
  }

  async updateStatuteArticle(
    statuteId: string,
    articleId: string,
    input: import('../lib/api').StatuteArticleInput,
  ) {
    this.guard('updateStatuteArticle');
    const statute = await this.getStatute(statuteId);
    const updated = { ...makeArticle(input), id: articleId, rowVersion: 2 };
    this.replaceStatute({
      ...statute,
      articles: statute.articles.map((article) => (article.id === articleId ? updated : article)),
    });
    return updated;
  }

  // ---- Negotiation -------------------------------------------------------------------------------

  async listCases(query: {
    page: number;
    pageSize: number;
    state?: import('../lib/api').CaseState | null;
  }): Promise<Page<import('../lib/api').NegotiationCaseSummary>> {
    this.guard('listCases');
    const summaries = this.cases
      .filter((negotiationCase) => (query.state ? negotiationCase.state === query.state : true))
      .map((negotiationCase) => ({
        id: negotiationCase.id,
        companyId: negotiationCase.companyId,
        startedOn: negotiationCase.startedOn,
        state: negotiationCase.state,
        closedAt: negotiationCase.closedAt,
        promotedAgreementId: negotiationCase.promotedAgreementId,
        partyCount: negotiationCase.parties.length,
        petitionCount: negotiationCase.petitions.length,
        meetingCount: negotiationCase.meetings.length,
        meetingAgreementCount: negotiationCase.petitions.reduce((sum, p) => sum + p.agreements.length, 0),
      }));
    return pageOf(summaries, query.page, query.pageSize);
  }

  async getCase(id: string): Promise<NegotiationCaseDetail> {
    this.guard('getCase');
    return this.cases.find((candidate) => candidate.id === id) ?? missing(id);
  }

  async createCase(startedOn: string): Promise<NegotiationCaseDetail> {
    this.guard('createCase');
    const negotiationCase = makeCase({ startedOn });
    this.cases = [negotiationCase, ...this.cases];
    return negotiationCase;
  }

  async transitionCase(id: string, step: import('../lib/api').CaseTransition): Promise<NegotiationCaseDetail> {
    this.guard('transitionCase');
    const negotiationCase = await this.getCase(id);
    const nextState: Record<string, NegotiationCaseDetail['state']> = {
      'in-progress': 'InProgress',
      pending: 'Pending',
      agree: 'Agreed',
      close: 'Closed',
      reopen: 'InProgress',
    };
    const updated: NegotiationCaseDetail = {
      ...negotiationCase,
      state: nextState[step] ?? negotiationCase.state,
      closedAt: step === 'close' ? '2026-02-01T10:00:00Z' : step === 'reopen' ? null : negotiationCase.closedAt,
    };
    this.replaceCase(updated);
    return updated;
  }

  async promoteCase(id: string, _input: import('../lib/api').PromoteCaseInput) {
    this.guard('promoteCase');
    const negotiationCase = await this.getCase(id);
    const agreementId = nextId('agreement');
    this.replaceCase({ ...negotiationCase, promotedAgreementId: agreementId });
    return { agreementId, caseId: id };
  }

  async addPetition(caseId: string, input: { number: number; title: string; body: string }): Promise<PetitionView> {
    this.guard('addPetition');
    const negotiationCase = await this.getCase(caseId);
    const petition = makePetition(input);
    this.replaceCase({ ...negotiationCase, petitions: [...negotiationCase.petitions, petition] });
    return petition;
  }

  async addMeeting(caseId: string, input: { heldOn: string; summary?: string | null }): Promise<MeetingView> {
    this.guard('addMeeting');
    const negotiationCase = await this.getCase(caseId);
    const meeting: MeetingView = {
      id: nextId('meeting'),
      heldOn: input.heldOn,
      startTime: null,
      duration: null,
      summary: input.summary ?? null,
      details: null,
      attendees: null,
    };
    this.replaceCase({ ...negotiationCase, meetings: [...negotiationCase.meetings, meeting] });
    return meeting;
  }

  async addMeetingAgreement(
    caseId: string,
    petitionId: string,
    input: { meetingId: string; agreedOn: string; number: number; title: string; body: string },
  ): Promise<MeetingAgreementView> {
    this.guard('addMeetingAgreement');
    const negotiationCase = await this.getCase(caseId);
    const agreement: MeetingAgreementView = {
      id: nextId('meeting-agreement'),
      petitionId,
      meetingId: input.meetingId,
      agreedOn: input.agreedOn,
      number: input.number,
      title: input.title,
      body: input.body,
      headingId: null,
    };
    this.replaceCase({
      ...negotiationCase,
      petitions: negotiationCase.petitions.map((petition) =>
        petition.id === petitionId
          ? { ...petition, agreements: [...petition.agreements, agreement] }
          : petition,
      ),
    });
    return agreement;
  }

  // ---- News --------------------------------------------------------------------------------------

  async listNews(query: {
    page: number;
    pageSize: number;
    state?: 'Draft' | 'Published' | null;
  }): Promise<Page<NewsSummary>> {
    this.guard('listNews');
    const summaries = this.news
      .filter((article) => (query.state ? article.state === query.state : true))
      .map((article) => ({
        id: article.id,
        title: article.title,
        summary: article.summary,
        categoryId: article.categoryId,
        categoryName: article.categoryName,
        state: article.state,
        publishedAt: article.publishedAt,
        updatedAt: article.updatedAt,
      }));
    return pageOf(summaries, query.page, query.pageSize);
  }

  async createNews(input: {
    categoryId: string;
    title: string;
    summary?: string | null;
    body: string;
  }): Promise<NewsDetail> {
    this.guard('createNews');
    const article = makeNewsDetail({
      categoryId: input.categoryId,
      categoryName: this.newsCategories.find((category) => category.id === input.categoryId)?.name ?? 'Noticias',
      title: input.title,
      summary: input.summary ?? null,
      body: input.body,
    });
    this.news = [article, ...this.news];
    return article;
  }

  async publishNews(id: string): Promise<NewsDetail> {
    this.guard('publishNews');
    const article = this.news.find((candidate) => candidate.id === id) ?? missing(id);
    const updated = { ...article, state: 'Published' as const, publishedAt: '2026-02-01T10:00:00Z' };
    this.replaceNews(updated);
    return updated;
  }

  async unpublishNews(id: string): Promise<NewsDetail> {
    this.guard('unpublishNews');
    const article = this.news.find((candidate) => candidate.id === id) ?? missing(id);
    const updated = { ...article, state: 'Draft' as const, publishedAt: null };
    this.replaceNews(updated);
    return updated;
  }

  async listNewsCategories(): Promise<NewsCategoryView[]> {
    this.guard('listNewsCategories');
    return this.newsCategories;
  }

  // ---- Administration ----------------------------------------------------------------------------

  async listUsers(query: {
    page: number;
    pageSize: number;
    q?: string | null;
  }): Promise<Page<UserSummary>> {
    this.guard('listUsers');
    const filtered = this.users.filter((user) =>
      query.q ? user.email.toLowerCase().includes(query.q.toLowerCase()) || user.displayName.toLowerCase().includes(query.q.toLowerCase()) : true,
    );
    return pageOf(filtered, query.page, query.pageSize);
  }

  async getUser(id: string): Promise<UserDetail> {
    this.guard('getUser');
    return this.users.find((user) => user.id === id) ?? missing(id);
  }

  async createUser(input: {
    subject: string;
    email: string;
    displayName: string;
    roleKeys: string[];
  }): Promise<UserDetail> {
    this.guard('createUser');
    const user = makeUser({ ...input, id: input.subject, roles: input.roleKeys });
    this.users = [...this.users, user];
    return user;
  }

  async updateUser(
    id: string,
    input: { displayName?: string; email?: string; isBlocked?: boolean },
  ): Promise<UserDetail> {
    this.guard('updateUser');
    const user = await this.getUser(id);
    const updated = { ...user, ...input };
    this.users = this.users.map((candidate) => (candidate.id === id ? updated : candidate));
    return updated;
  }

  async replaceUserRoles(id: string, roleKeys: string[]): Promise<UserDetail> {
    this.guard('replaceUserRoles');
    const user = await this.getUser(id);
    const updated = { ...user, roles: roleKeys };
    this.users = this.users.map((candidate) => (candidate.id === id ? updated : candidate));
    return updated;
  }

  async listRoles(): Promise<RoleView[]> {
    this.guard('listRoles');
    return this.roles;
  }

  async listCapabilities(): Promise<CapabilityView[]> {
    this.guard('listCapabilities');
    return this.capabilities;
  }

  async listAudit(query: {
    page: number;
    pageSize: number;
    entityType?: string | null;
    module?: string | null;
  }): Promise<Page<AuditEntryView>> {
    this.guard('listAudit');
    const filtered = this.audit.filter((entry) =>
      (query.entityType ? entry.entityType === query.entityType : true) &&
      (query.module ? entry.module === query.module : true),
    );
    return pageOf(filtered, query.page, query.pageSize);
  }

  // ---- Helpers -----------------------------------------------------------------------------------

  private replaceAgreement(agreement: AgreementDetail): void {
    this.agreements = this.agreements.map((candidate) => (candidate.id === agreement.id ? agreement : candidate));
  }

  private replaceStatute(statute: StatuteDetail): void {
    this.statutes = this.statutes.map((candidate) => (candidate.id === statute.id ? statute : candidate));
  }

  private replaceCase(negotiationCase: NegotiationCaseDetail): void {
    this.cases = this.cases.map((candidate) => (candidate.id === negotiationCase.id ? negotiationCase : candidate));
  }

  private replaceNews(article: NewsDetail): void {
    this.news = this.news.map((candidate) => (candidate.id === article.id ? article : candidate));
  }
}

/** The anonymous client. It has no token and no way to obtain one. */
export class FakePublicApi implements PublicApi {
  agreements: PublicAgreementSummary[] = [];
  agreementDetails = new Map<string, PublicAgreementDetail>();
  news: PublicNewsSummary[] = [];
  newsDetails = new Map<string, PublicNewsDetail>();
  calls = 0;

  async listAgreements(page: number, pageSize: number): Promise<Page<PublicAgreementSummary>> {
    this.calls += 1;
    return pageOf(this.agreements, page, pageSize);
  }

  async getAgreement(id: string): Promise<PublicAgreementDetail> {
    this.calls += 1;
    return this.agreementDetails.get(id) ?? missing(id);
  }

  async listNews(page: number, pageSize: number): Promise<Page<PublicNewsSummary>> {
    this.calls += 1;
    return pageOf(this.news, page, pageSize);
  }

  async getNews(id: string): Promise<PublicNewsDetail> {
    this.calls += 1;
    return this.newsDetails.get(id) ?? missing(id);
  }
}
