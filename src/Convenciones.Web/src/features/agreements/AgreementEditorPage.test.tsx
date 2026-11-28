import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';
import { FakeApi, makeAgreementDetail, makeHeading } from '../../test/fakes';

/**
 * Regression for #135: the clause editor reads `/comparison/headings`, which answers with groups that nest
 * their headings. The selector must offer real heading ids, and an excluded heading stays a valid
 * classification for a clause ("No Compara" held 853 legacy clauses), so it is offered and labelled
 * instead of hidden.
 */
describe('AgreementEditorPage', () => {
  it('offers real heading ids and labels the excluded heading', async () => {
    const api = new FakeApi();
    const agreement = makeAgreementDetail();
    api.agreements = [agreement];
    api.headings = [
      makeHeading({ name: 'Vacaciones anuales', groupName: 'GENERALES' }),
      makeHeading({ name: 'No Compara', groupName: 'GENERALES', isExcluded: true }),
    ];

    const { user } = renderApp({ api, route: `/agreements/${agreement.id}` });

    await user.click(await screen.findByRole('button', { name: 'Añadir cláusula' }));

    const selector = await screen.findByLabelText(/materia/i);
    await user.click(selector);

    expect(await screen.findByText(/No Compara \(no se compara\)/)).toBeInTheDocument();
    expect(screen.getByText(/GENERALES · Vacaciones anuales/)).toBeInTheDocument();
  });
});