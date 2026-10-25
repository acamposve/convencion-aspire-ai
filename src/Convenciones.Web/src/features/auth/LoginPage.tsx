import { useState, type FormEvent } from 'react';
import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import Container from '@mui/material/Container';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { Navigate } from 'react-router-dom';
import { useServices } from '../../services';
import type { UiError } from '../../lib/errors';
import { ErrorNotice } from '../../components/ErrorNotice';
import { LoadingScreen } from '../../components/LoadingScreen';
import { useSession } from './SessionProvider';
import { UnauthorizedPage } from './UnauthorizedPage';

/**
 * Sign-in.
 *
 * When Supabase is configured, the email/password form delegates to Supabase Auth. When it is not
 * (the repository's state until T18), the page says so with the code `auth_not_configured` instead
 * of pretending to authenticate. In development only, a token minted elsewhere can be pasted; it is
 * kept in memory and still validated by the API.
 *
 * Errors are displayed by code, never as a raw provider message.
 */
export function LoginPage() {
  const { status, error, signIn, signInWithToken } = useSession();
  const { auth } = useServices();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [localError, setLocalError] = useState<UiError | null>(null);
  const [devToken, setDevToken] = useState('');
  const [tokenSubmitting, setTokenSubmitting] = useState(false);

  if (status === 'loading') {
    return <LoadingScreen label="Cargando sesión…" />;
  }

  if (status === 'authenticated') {
    return <Navigate to="/documents" replace />;
  }

  if (status === 'unauthorized') {
    return <UnauthorizedPage />;
  }

  const visibleError = localError ?? error;

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (email.trim() === '' || password === '') {
      setLocalError({
        code: 'validation_failed',
        detail: 'Correo y contraseña son obligatorios.',
      });
      return;
    }

    setLocalError(null);
    setSubmitting(true);

    try {
      await signIn(email.trim(), password);
    } finally {
      setSubmitting(false);
    }
  };

  const handleTokenSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (devToken.trim() === '') {
      setLocalError({ code: 'validation_failed', detail: 'El token no puede estar vacío.' });
      return;
    }

    setLocalError(null);
    setTokenSubmitting(true);

    try {
      await signInWithToken(devToken.trim());
    } finally {
      setTokenSubmitting(false);
    }
  };

  return (
    <Container maxWidth="sm" sx={{ py: { xs: 4, sm: 8 }, px: 2 }}>
      <Paper variant="outlined" sx={{ p: { xs: 2.5, sm: 4 } }}>
        <Typography variant="h4" component="h1" gutterBottom>
          Iniciar sesión
        </Typography>
        <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
          Acceda con su cuenta de Convenciones para gestionar contratos colectivos.
        </Typography>

        {!auth.isConfigured ? (
          <Alert severity="warning" sx={{ mb: 3 }}>
            <AlertTitle>Autenticación no configurada</AlertTitle>
            Falta configurar el proyecto de Supabase. Defina <code>VITE_SUPABASE_URL</code> y{' '}
            <code>VITE_SUPABASE_ANON_KEY</code>.
            <Box component="span" sx={{ display: 'block', mt: 1 }}>
              Código: <strong>auth_not_configured</strong>
            </Box>
          </Alert>
        ) : (
          <Box component="form" noValidate onSubmit={handleSubmit} aria-label="Formulario de acceso">
            <Stack spacing={2}>
              <TextField
                label="Correo electrónico"
                type="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                autoComplete="username"
                autoFocus
                required
              />
              <TextField
                label="Contraseña"
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                autoComplete="current-password"
                required
              />
              <Button
                type="submit"
                variant="contained"
                size="large"
                disabled={submitting}
                startIcon={submitting ? <CircularProgress size={18} color="inherit" /> : undefined}
              >
                Entrar
              </Button>
            </Stack>
          </Box>
        )}

        {import.meta.env.DEV ? (
          <Box component="form" onSubmit={handleTokenSubmit} sx={{ mt: 3 }} aria-label="Sesión de desarrollo">
            <Typography variant="subtitle2" component="h2" gutterBottom>
              Sesión de desarrollo
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
              Solo disponible en desarrollo. Pegue un token ya emitido para probar contra el API; se
              mantiene en memoria y nunca se guarda en el navegador.
            </Typography>
            <Stack spacing={1.5}>
              <TextField
                label="Token de acceso (solo desarrollo)"
                value={devToken}
                onChange={(event) => setDevToken(event.target.value)}
                multiline
                minRows={2}
                autoComplete="off"
              />
              <Button type="submit" variant="outlined" disabled={tokenSubmitting}>
                Usar token
              </Button>
            </Stack>
          </Box>
        ) : null}

        {visibleError ? <ErrorNotice error={visibleError} title="No se pudo iniciar sesión" /> : null}
      </Paper>
    </Container>
  );
}
