import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, makeArticle, makeHeading, makeStatute } from '../../test/fakes';

/**
 * Regression for #135 on the statute page: it shares `listHeadings()` with the comparison and the clause
 * editor, so it received group ids as heading ids too. The selector must list group · heading pairs and
 * label the excluded heading, which remains a valid classification for an article.
 */
describe('StatutePage', () => {
  it('offers real heading ids and labels the excluded heading', async () => {
    const api = new FakeApi();
    const statute = makeStatute({ articles: [makeArticle({ number: 1 })] });
    api.statutes = [statute];
    api.headings = [
      makeHeading({ name: 'Prevención de riesgos laborales', groupName: 'SEGURIDAD OCUPACIONAL' }),
      makeHeading({ name: 'No Compara', groupName: 'GENERALES', isExcluded: true }),
    ];

    const { user } = renderApp({ api, route: `/legal/${statute.id}` });

    await user.click(await screen.findByRole('button', { name: 'Editar artículo 1' }));

    const selector = await screen.findByLabelText(/materia \(opcional\)/i);
    await user.click(selector);

    expect(
      await screen.findByText(/SEGURIDAD OCUPACIONAL · Prevención de riesgos laborales/),
    ).toBeInTheDocument();
    expect(screen.getByText(/No Compara \(no se compara\)/)).toBeInTheDocument();
  });
});