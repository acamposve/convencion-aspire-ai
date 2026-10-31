import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { App } from './App';
import { HttpApiClient, HttpPublicApi } from './lib/api';
import { createAuthClient } from './lib/auth';
import './styles.css';

const baseUrl = import.meta.env.VITE_API_BASE_URL ?? '';
const auth = createAuthClient();
const api = new HttpApiClient(baseUrl, auth);
// The portal client sends no token, by construction: there is nothing to send.
const publicApi = new HttpPublicApi(baseUrl);
const services = { auth, api, publicApi };

const container = document.getElementById('root');

if (container === null) {
  throw new Error('Root container is missing from index.html.');
}

createRoot(container).render(
  <BrowserRouter>
    <App services={services} />
  </BrowserRouter>,
);
