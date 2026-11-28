import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, makeHeading } from '../../test/fakes';
import type { ComparisonCandidate, HeadingView, Page } from '../../lib/api';
import { ApiError } from '../../lib/errors';

const heading = makeHeading({ name: 'Salarios', groupName: 'GENERALES' });

function seed(api: FakeApi) {
  api.headings = [heading];
  api.candidates = [
    {
      agreementId: 'a1',
      companyId: 'c1',
      companyName: 'Empresa Uno',
      title: 'Empresa Uno · 2026',
      validFrom: '2026-01-01',
      validTo: null,
      clauseCount: 1,
    },
  ];
  api.comparison = {
    heading,
    statuteArticles: [
      {
        statuteId: 's1',
        statuteName: 'Ley Orgánica del Trabajo',
        statuteKind: 'LabourLaw',
        articleId: 'art1',
        number: 129,
        title: 'Salario mínimo',
        body: 'Texto del artículo.',
        economicComparisonNote: null,
      },
    ],
    statuteArticleTotal: 1,
    agreements: [
      {
        agreementId: 'a1',
        companyId: 'c1',
        companyName: 'Empresa Uno',
        title: 'Empresa Uno · 2026',
        validFrom: '2026-01-01',
        validTo: null,
        clauses: [
          {
            clauseId: 'cl1',
            number: 1,
            title: 'Salario básico',
            summary: null,
            body: 'Texto de la cláusula.',
            economicComparisonNote: null,
            isExcludedFromCompilation: false,
          },
        ],
      },
    ],
    unpublishedRequested: ['a2'],
    page: 1,
    pageSize: 5,
    total: 1,
  };
}

describe('ComparisonPage', () => {
  it('shows clauses adjacent by heading and claims no alignment', async () => {
    const api = new FakeApi();
    seed(api);
    const { user } = renderApp({ api, route: '/comparison' });

    const checkbox = await screen.findByRole('checkbox');
    await user.click(checkbox);
    expect(checkbox).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    expect(await screen.findByText(/Salario básico/)).toBeInTheDocument();
    expect(screen.getByText(/Vecindad por materia, sin alineación/i)).toBeInTheDocument();
    expect(screen.getByText(/requiere comparación económica/i)).toBeInTheDocument();

    // The screen must not assert alignment between clauses: the only mention is the disclaimer that
    // denies it, and no element describes a clause as aligned.
    const body = document.body.textContent ?? '';
    expect(body).not.toMatch(/alinead[oa]/i);
    expect(body).toContain('no porque sean equivalentes');
  });

  it('warns about requested agreements that are not published', async () => {
    const api = new FakeApi();
    seed(api);
    const { user } = renderApp({ api, route: '/comparison' });

    await user.click(await screen.findByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    expect(await screen.findByText(/no están publicados/i)).toBeInTheDocument();
  });

  it('never offers an excluded heading as a comparable matter', async () => {
    const api = new FakeApi();
    api.headings = [
      makeHeading({ name: 'No Compara', isExcluded: true }),
      makeHeading({ name: 'Vacaciones anuales' }),
    ];

    renderApp({ api, route: '/comparison' });

    expect(await screen.findByText(/GENERALES · Vacaciones anuales/)).toBeInTheDocument();
    expect(screen.queryByText(/No Compara/)).toBeNull();
  });

  it('recovers to the first usable heading when the selected one disappeared', async () => {
    const replacement = makeHeading({ name: 'Permisos y licencias' });
    const api = new StaleHeadingApi(replacement);
    api.headings = [makeHeading({ name: 'Vacaciones anuales' })];

    renderApp({ api, route: '/comparison' });

    expect(await screen.findByText(/ya no está disponible/i)).toBeInTheDocument();
    expect(await screen.findByText(/Permisos y licencias/)).toBeInTheDocument();
  });

  it('shows the validation problem and keeps the screen usable when the API rejects the heading', async () => {
    const api = new RejectingHeadingApi();
    api.headings = [makeHeading({ name: 'Vacaciones anuales' })];

    renderApp({ api, route: '/comparison' });

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toMatch(/GUID/);
    expect(screen.getByRole('combobox')).toBeInTheDocument();
  });
});

/** A heading that vanishes after the first candidates request: the stale-selection case of #135. */
class StaleHeadingApi extends FakeApi {
  private candidateCalls = 0;

  constructor(private readonly replacement: HeadingView) {
    super();
  }

  override async listCandidates(): Promise<Page<ComparisonCandidate>> {
    this.candidateCalls += 1;

    if (this.candidateCalls === 1) {
      this.headings = [this.replacement];
      throw new ApiError(400, 'validation_failed', "No active heading with id 'gone' exists.");
    }

    return { items: [], page: 1, pageSize: 50, total: 0 };
  }
}

/** The API rejects every candidates request with the documented validation problem. */
class RejectingHeadingApi extends FakeApi {
  override async listCandidates(): Promise<Page<ComparisonCandidate>> {
    throw new ApiError(400, 'validation_failed', 'headingId must be a GUID.');
  }
}
