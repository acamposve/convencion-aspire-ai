import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { LoadingScreen } from '../../components/LoadingScreen';
import { AppShell } from '../../components/AppShell';
import { useSession } from './SessionProvider';
import { UnauthorizedPage } from './UnauthorizedPage';

export function RequireSession() {
  const { status } = useSession();
  const location = useLocation();

  if (status === 'loading') {
    return <LoadingScreen />;
  }

  if (status === 'unauthenticated') {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  if (status === 'unauthorized') {
    return <UnauthorizedPage />;
  }

  return (
    <AppShell>
      <Outlet />
    </AppShell>
  );
}
