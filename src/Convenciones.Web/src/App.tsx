import { ThemeProvider } from '@mui/material/styles';
import CssBaseline from '@mui/material/CssBaseline';
import { Suspense, lazy } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { theme } from './theme';
import { ServicesProvider, type AppServices } from './services';
import { SessionProvider } from './features/auth/SessionProvider';
import { LoginPage } from './features/auth/LoginPage';
import { RequireSession } from './features/auth/RequireSession';
import { DocumentsPage } from './features/documents/DocumentsPage';
import { LoadingScreen } from './components/LoadingScreen';

// The feature screens are loaded on demand: the initial bundle carries the shell and the document
// workflow, and a section the caller never opens costs nothing until they do.
const AgreementsPage = lazy(() =>
  import('./features/agreements/AgreementsPage').then((module) => ({ default: module.AgreementsPage })),
);
const AgreementEditorPage = lazy(() =>
  import('./features/agreements/AgreementEditorPage').then((module) => ({ default: module.AgreementEditorPage })),
);
const ComparisonPage = lazy(() =>
  import('./features/comparison/ComparisonPage').then((module) => ({ default: module.ComparisonPage })),
);
const LegalLibraryPage = lazy(() =>
  import('./features/legal/LegalLibraryPage').then((module) => ({ default: module.LegalLibraryPage })),
);
const StatutePage = lazy(() =>
  import('./features/legal/LegalLibraryPage').then((module) => ({ default: module.StatutePage })),
);
const NegotiationPage = lazy(() =>
  import('./features/negotiation/NegotiationPage').then((module) => ({ default: module.NegotiationPage })),
);
const NegotiationCasePage = lazy(() =>
  import('./features/negotiation/NegotiationPage').then((module) => ({ default: module.NegotiationCasePage })),
);
const NewsPage = lazy(() =>
  import('./features/news/NewsPage').then((module) => ({ default: module.NewsPage })),
);
const UsersPage = lazy(() =>
  import('./features/admin/AdminPages').then((module) => ({ default: module.UsersPage })),
);
const RolesPage = lazy(() =>
  import('./features/admin/AdminPages').then((module) => ({ default: module.RolesPage })),
);
const AuditPage = lazy(() =>
  import('./features/admin/AdminPages').then((module) => ({ default: module.AuditPage })),
);
const PortalPage = lazy(() =>
  import('./features/public/PortalPage').then((module) => ({ default: module.PortalPage })),
);

export function App({ services }: { services: AppServices }) {
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <ServicesProvider services={services}>
        <SessionProvider>
          <Suspense fallback={<LoadingScreen label="Cargando sección…" />}>
          <Routes>
            {/* The public portal is anonymous: no session, no token, no shell. */}
            <Route path="/portal" element={<PortalPage />} />

            <Route path="/login" element={<LoginPage />} />
            <Route element={<RequireSession />}>
              <Route path="/" element={<Navigate to="/documents" replace />} />
              <Route path="/documents" element={<DocumentsPage />} />
              <Route path="/agreements" element={<AgreementsPage />} />
              <Route path="/agreements/:id" element={<AgreementEditorPage />} />
              <Route path="/comparison" element={<ComparisonPage />} />
              <Route path="/legal" element={<LegalLibraryPage />} />
              <Route path="/legal/:statuteId" element={<StatutePage />} />
              <Route path="/negotiation" element={<NegotiationPage />} />
              <Route path="/negotiation/:caseId" element={<NegotiationCasePage />} />
              <Route path="/news" element={<NewsPage />} />
              <Route path="/admin/users" element={<UsersPage />} />
              <Route path="/admin/roles" element={<RolesPage />} />
              <Route path="/audit" element={<AuditPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
          </Suspense>
        </SessionProvider>
      </ServicesProvider>
    </ThemeProvider>
  );
}
