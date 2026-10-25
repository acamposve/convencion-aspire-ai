import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { installMatchMedia } from '../../test/media';
import { FakeApi, FakeAuth } from '../../test/fakes';
import { renderApp } from '../../test/renderApp';

function renderDocuments(api = new FakeApi()) {
  return renderApp({
    auth: new FakeAuth({ token: 'fake-token' }),
    api,
    route: '/documents',
  });
}

describe('Accessibility and responsive shell', () => {
  it('provides a skip link to the main content', async () => {
    renderDocuments();

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Saltar al contenido' })).toHaveAttribute(
      'href',
      '#contenido',
    );
    expect(document.querySelector('#contenido')).not.toBeNull();
  });

  it('opens the file picker from the upload zone with the keyboard', async () => {
    const { user } = renderDocuments();

    const zone = await screen.findByRole('button', { name: /Subir un documento PDF/i });
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {});

    zone.focus();
    expect(zone).toHaveFocus();

    await user.keyboard('{Enter}');

    expect(click).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('file-input')).toHaveAttribute('accept', '.pdf,application/pdf');
  });

  it('uses the compact shell at a 360 px viewport', async () => {
    installMatchMedia(true);
    renderDocuments();

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();
    expect(document.querySelector('[data-layout="compact"]')).not.toBeNull();
  });

  it('uses the wide shell at a desktop viewport', async () => {
    installMatchMedia(false);
    renderDocuments();

    expect(await screen.findByRole('heading', { name: 'Documentos' })).toBeInTheDocument();
    expect(document.querySelector('[data-layout="wide"]')).not.toBeNull();
  });
});
