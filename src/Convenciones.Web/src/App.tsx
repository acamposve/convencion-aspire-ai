import { ThemeProvider } from '@mui/material/styles';
import CssBaseline from '@mui/material/CssBaseline';
import { Navigate, Route, Routes } from 'react-router-dom';
import { theme } from './theme';
import { ServicesProvider, type AppServices } from './services';
import { SessionProvider } from './features/auth/SessionProvider';
import { LoginPage } from './features/auth/LoginPage';
import { RequireSession } from './features/auth/RequireSession';
import { DocumentsPage } from './features/documents/DocumentsPage';

export function App({ services }: { services: AppServices }) {
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <ServicesProvider services={services}>
        <SessionProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route element={<RequireSession />}>
              <Route path="/" element={<Navigate to="/documents" replace />} />
              <Route path="/documents" element={<DocumentsPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </SessionProvider>
      </ServicesProvider>
    </ThemeProvider>
  );
}
