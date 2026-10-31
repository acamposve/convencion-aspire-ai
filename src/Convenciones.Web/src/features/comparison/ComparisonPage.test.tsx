import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, makeHeading } from '../../test/fakes';

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
});
