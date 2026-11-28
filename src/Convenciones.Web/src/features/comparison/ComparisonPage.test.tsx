import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, makeHeading } from '../../test/fakes';
import type { ComparisonCandidate, HeadingView, Page } from '../../lib/api';
import { ApiError } from '../../lib/errors';

const heading = makeHeading({
  name: 'Salarios',
  groupName: 'GENERALES',
  groupDescription: 'Se agrupan cláusulas que establecen condiciones y definiciones entre las partes.',
});

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
      clauseCount: 2,
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
          {
            clauseId: 'cl2',
            number: 2,
            title: 'Prima de antigüedad',
            summary: 'Resumen de la prima.',
            body: 'Texto completo de la prima de antigüedad.',
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

    expect(await screen.findByText(/no están publicadas/i)).toBeInTheDocument();
  });

  it('shows the definition of the selected materia', async () => {
    const api = new FakeApi();
    seed(api);
    renderApp({ api, route: '/comparison' });

    expect(await screen.findByTestId('heading-definition')).toHaveTextContent(
      'Se agrupan cláusulas que establecen condiciones y definiciones entre las partes.',
    );
  });

  it('shows the clause summary and expands the full text on demand', async () => {
    const api = new FakeApi();
    seed(api);
    const { user } = renderApp({ api, route: '/comparison' });

    await user.click(await screen.findByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    // The clause with a summary shows the summary, not the full body.
    expect(await screen.findByText('Resumen de la prima.')).toBeInTheDocument();
    expect(screen.queryByTestId('clause-body')).toBeNull();

    const toggle = screen.getByRole('button', { name: 'Ver cláusula completa' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await user.click(toggle);

    expect(screen.getByTestId('clause-body')).toHaveTextContent(
      'Texto completo de la prima de antigüedad.',
    );
    expect(screen.queryByText('Resumen de la prima.')).toBeNull();

    const collapse = screen.getByRole('button', { name: 'Ocultar cláusula completa' });
    expect(collapse).toHaveAttribute('aria-expanded', 'true');
    await user.click(collapse);

    // Collapse unmounts the body when its exit transition ends, so the assertion waits for the DOM
    // rather than racing the animation.
    await waitFor(() => expect(screen.queryByTestId('clause-body')).toBeNull());
    expect(screen.getByText('Resumen de la prima.')).toBeInTheDocument();
  });

  it('does not offer to expand a clause whose body is the summary', async () => {
    const api = new FakeApi();
    seed(api);
    const { user } = renderApp({ api, route: '/comparison' });

    await user.click(await screen.findByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    // The first clause has no summary, so its body is the base text and there is nothing to expand.
    expect(await screen.findByText('Texto de la cláusula.')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Ver cláusula completa' })).toHaveLength(1);
  });

  it('shows the validity of each agreement with its duration', async () => {
    const api = new FakeApi();
    seed(api);
    api.comparison!.agreements[0]!.validTo = '2027-01-01';
    const { user } = renderApp({ api, route: '/comparison' });

    await user.click(await screen.findByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    expect(await screen.findByText(/vigencia 2026-01-01 – 2027-01-01 · 1 año/)).toBeInTheDocument();
  });

  it('keeps an open-ended agreement without inventing an end date', async () => {
    const api = new FakeApi();
    seed(api);
    const { user } = renderApp({ api, route: '/comparison' });

    await user.click(await screen.findByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    expect(await screen.findByText(/vigencia 2026-01-01 – sin fecha fin/)).toBeInTheDocument();
  });

  it('places the legal articles under the materia, never paired with a clause', async () => {
    const api = new FakeApi();
    seed(api);
    const { user } = renderApp({ api, route: '/comparison' });

    await user.click(await screen.findByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Comparar' }));

    expect(await screen.findByRole('heading', { name: 'Biblioteca legal de esta materia' })).toBeInTheDocument();
    expect(screen.getByText(/no hay emparejamiento uno a uno entre artículo y cláusula/i)).toBeInTheDocument();
    // The article is presented as adjacent to the materia, not as the source of a specific clause.
    expect(screen.getByText(/Ley Orgánica del Trabajo · art. 129/)).toBeInTheDocument();
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
