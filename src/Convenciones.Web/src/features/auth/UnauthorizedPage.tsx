import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Paper from '@mui/material/Paper';
import Typography from '@mui/material/Typography';
import { useSession } from './SessionProvider';

/**
 * Shown when the identity provider accepts the caller but this application has no usable account:
 * no profile, blocked, expired, or missing a required capability. The API's code is the explanation.
 */
export function UnauthorizedPage() {
  const { error, signOut } = useSession();

  return (
    <Container maxWidth="sm" sx={{ py: { xs: 4, sm: 8 }, px: 2 }}>
      <Paper variant="outlined" sx={{ p: { xs: 2.5, sm: 4 } }}>
        <Typography variant="h4" component="h1" gutterBottom>
          Acceso no autorizado
        </Typography>
        <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
          Su identidad es válida, pero esta cuenta no tiene acceso a Convenciones. Un administrador
          debe habilitarla.
        </Typography>

        <Alert severity="warning" role="alert">
          <AlertTitle>La sesión no tiene un perfil utilizable</AlertTitle>
          <Box component="span" sx={{ display: 'block' }}>
            {error?.detail ?? 'El API rechazó la sesión.'}
          </Box>
          <Box component="span" sx={{ display: 'block', mt: 1, fontFamily: 'ui-monospace, monospace' }}>
            Código: <strong>{error?.code ?? 'forbidden'}</strong>
            {error?.status !== undefined ? ` · HTTP ${error.status}` : ''}
          </Box>
        </Alert>

        <Box sx={{ mt: 3, display: 'flex', justifyContent: 'flex-end' }}>
          <Button variant="contained" onClick={() => void signOut()}>
            Cerrar sesión
          </Button>
        </Box>
      </Paper>
    </Container>
  );
}
