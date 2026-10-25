import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { App } from './App';
import { HttpApiClient } from './lib/api';
import { createAuthClient } from './lib/auth';
import './styles.css';

const auth = createAuthClient();
const api = new HttpApiClient(import.meta.env.VITE_API_BASE_URL ?? '', auth);
const services = { auth, api };

const container = document.getElementById('root');

if (container === null) {
  throw new Error('Root container is missing from index.html.');
}

createRoot(container).render(
  <BrowserRouter>
    <App services={services} />
  </BrowserRouter>,
);
