import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { ApiError, AuthError, describeError, type UiError } from '../../lib/errors';
import type { Me } from '../../lib/api';
import { useServices } from '../../services';

export type SessionStatus = 'loading' | 'unauthenticated' | 'authenticated' | 'unauthorized';

interface SessionContextValue {
  status: SessionStatus;
  me: Me | null;
  error: UiError | null;
  signIn(email: string, password: string): Promise<void>;
  signInWithToken(token: string): Promise<void>;
  signOut(): Promise<void>;
  clearError(): void;
}

const SessionContext = createContext<SessionContextValue | null>(null);

/**
 * Resolves the caller once per page load and keeps the token in memory only.
 *
 * Signing in is not enough: the API resolves the account, company and capabilities itself. A valid
 * Supabase token for an account that has no provisioned profile is reported as unauthorised, with
 * the API's code, rather than being treated as a session.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const { auth, api } = useServices();
  const [status, setStatus] = useState<SessionStatus>('loading');
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<UiError | null>(null);

  const loadSession = useCallback(async (): Promise<void> => {
    const token = await auth.getAccessToken();

    if (!token) {
      setMe(null);
      setStatus('unauthenticated');
      return;
    }

    try {
      const profile = await api.getMe();
      setMe(profile);
      setError(null);
      setStatus('authenticated');
    } catch (failure) {
      const described = describeError(failure);

      if (failure instanceof ApiError && failure.status === 403) {
        // Authenticated at the identity provider but not provisioned for this application.
        setMe(null);
        setError(described);
        setStatus('unauthorized');
        return;
      }

      setMe(null);
      setError(described);
      setStatus('unauthenticated');
      await auth.signOut();
    }
  }, [api, auth]);

  useEffect(() => {
    void loadSession();
  }, [loadSession]);

  const signIn = useCallback(
    async (email: string, password: string): Promise<void> => {
      setError(null);

      try {
        await auth.signIn(email, password);
        await loadSession();
      } catch (failure) {
        setMe(null);
        setError(describeError(failure));
        setStatus('unauthenticated');
        await auth.signOut();
      }
    },
    [auth, loadSession],
  );

  const signInWithToken = useCallback(
    async (token: string): Promise<void> => {
      setError(null);

      if (!auth.signInWithToken) {
        setError({
          code: 'auth_not_configured',
          detail: 'Este modo de sesión no está disponible en la compilación actual.',
        });
        return;
      }

      try {
        await auth.signInWithToken(token);
        await loadSession();
      } catch (failure) {
        setMe(null);
        setError(
          failure instanceof AuthError
            ? describeError(failure)
            : { code: 'invalid_token', detail: 'El token no pudo utilizarse.' },
        );
        setStatus('unauthenticated');
        await auth.signOut();
      }
    },
    [auth, loadSession],
  );

  const signOut = useCallback(async (): Promise<void> => {
    await auth.signOut();
    setMe(null);
    setError(null);
    setStatus('unauthenticated');
  }, [auth]);

  const value = useMemo<SessionContextValue>(
    () => ({
      status,
      me,
      error,
      signIn,
      signInWithToken,
      signOut,
      clearError: () => setError(null),
    }),
    [status, me, error, signIn, signInWithToken, signOut],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const session = useContext(SessionContext);

  if (session === null) {
    throw new Error('useSession must be used inside SessionProvider.');
  }

  return session;
}
