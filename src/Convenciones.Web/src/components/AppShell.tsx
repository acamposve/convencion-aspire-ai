import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Container from '@mui/material/Container';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import { useTheme } from '@mui/material/styles';
import AccountCircleIcon from '@mui/icons-material/AccountCircle';
import BusinessIcon from '@mui/icons-material/Business';
import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { useSession } from '../features/auth/SessionProvider';
import { useCapabilities } from '../features/auth/useCapabilities';
import { useServices } from '../services';
import { shortId } from '../lib/format';
import { LoadingScreen } from './LoadingScreen';

interface NavItem {
  to: string;
  label: string;
  capability: string;
}

/** Every destination, with the capability that makes it useful. Hiding it is a courtesy (AC 1). */
const NAV: NavItem[] = [
  { to: '/documents', label: 'Documentos', capability: 'document.read' },
  { to: '/agreements', label: 'Acuerdos', capability: 'agreement.read' },
  { to: '/comparison', label: 'Comparación', capability: 'comparison.read' },
  { to: '/legal', label: 'Biblioteca legal', capability: 'statute.read' },
  { to: '/negotiation', label: 'Negociación', capability: 'negotiation.read' },
  { to: '/news', label: 'Noticias', capability: 'news.read' },
  { to: '/admin/users', label: 'Usuarios', capability: 'user.manage' },
  { to: '/admin/roles', label: 'Roles', capability: 'role.manage' },
  { to: '/audit', label: 'Auditoría', capability: 'audit.read' },
];

/**
 * The application shell shared by every authenticated screen.
 *
 * The navigation shows only the sections the caller holds a capability for. The company indicator is
 * fed by `/me` (the authoritative session) and the company's name by `/companies/{id}`; if the name
 * cannot be read, the id is shown rather than an invented label.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { me, signOut } = useSession();
  const { has } = useCapabilities();
  const { api } = useServices();
  const theme = useTheme();
  const compact = useMediaQuery(theme.breakpoints.down('md'));
  const location = useLocation();
  const [companyName, setCompanyName] = useState<string | null>(null);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);

  const companyId = me?.companyId ?? null;

  useEffect(() => {
    if (companyId === null) {
      setCompanyName(null);
      return;
    }

    let cancelled = false;
    setCompanyName(`Empresa ${shortId(companyId)}`);

    void api
      .getCompany(companyId)
      .then((company) => {
        if (!cancelled) {
          setCompanyName(company.name);
        }
      })
      .catch(() => {
        // The fallback label is already displayed; a missing company name is not a session failure.
      });

    return () => {
      cancelled = true;
    };
  }, [api, companyId]);

  if (!me) {
    return <LoadingScreen label="Cargando sesión…" />;
  }

  const displayName = me.displayName ?? me.email;
  const companyLabel = companyId === null ? 'Sin empresa' : (companyName ?? `Empresa ${shortId(companyId)}`);
  const visible = NAV.filter((item) => has(item.capability));

  return (
    <Box
      data-layout={compact ? 'compact' : 'wide'}
      sx={{ minHeight: '100vh', display: 'flex', flexDirection: 'column', bgcolor: 'background.default' }}
    >
      <a className="skip-link" href="#contenido">
        Saltar al contenido
      </a>

      <AppBar position="sticky" elevation={0}>
        <Toolbar sx={{ gap: 1, px: { xs: 1.5, sm: 3 }, flexWrap: 'wrap' }}>
          <Button
            component={RouterLink}
            to="/"
            color="inherit"
            aria-label="Convenciones, ir al inicio"
            sx={{ px: 0.5, minWidth: 0 }}
          >
            <Typography variant="h6" component="span" sx={{ fontWeight: 700 }}>
              Convenciones
            </Typography>
          </Button>

          <Box component="nav" aria-label="Secciones" sx={{ display: 'flex', gap: 0.5, flexWrap: 'wrap' }}>
            {visible.map((item) => (
              <Button
                key={item.to}
                component={RouterLink}
                to={item.to}
                color="inherit"
                size="small"
                aria-current={location.pathname.startsWith(item.to) ? 'page' : undefined}
                sx={{ textTransform: 'none', opacity: location.pathname.startsWith(item.to) ? 1 : 0.8 }}
              >
                {item.label}
              </Button>
            ))}
            <Button
              component={RouterLink}
              to="/portal"
              color="inherit"
              size="small"
              sx={{ textTransform: 'none', opacity: 0.8 }}
            >
              Portal público
            </Button>
          </Box>

          <Box sx={{ flexGrow: 1 }} />

          <Chip
            icon={<BusinessIcon />}
            label={companyLabel}
            variant="outlined"
            size="small"
            sx={{
              color: 'inherit',
              borderColor: 'rgba(255,255,255,0.45)',
              maxWidth: { xs: 150, sm: 300 },
              '& .MuiChip-label': { overflow: 'hidden', textOverflow: 'ellipsis' },
            }}
            aria-label={`Empresa: ${companyLabel}`}
          />

          <IconButton
            color="inherit"
            aria-label={`Cuenta de ${displayName}`}
            aria-controls={menuAnchor ? 'account-menu' : undefined}
            aria-haspopup="true"
            aria-expanded={menuAnchor ? 'true' : undefined}
            onClick={(event) => setMenuAnchor(event.currentTarget)}
          >
            <AccountCircleIcon />
          </IconButton>

          <Menu
            id="account-menu"
            anchorEl={menuAnchor}
            open={menuAnchor !== null}
            onClose={() => setMenuAnchor(null)}
          >
            <MenuItem disabled sx={{ opacity: 1 }}>
              <Box>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>
                  {displayName}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  {me.email}
                </Typography>
              </Box>
            </MenuItem>
            <Divider />
            <MenuItem
              onClick={() => {
                setMenuAnchor(null);
                void signOut();
              }}
            >
              Cerrar sesión
            </MenuItem>
          </Menu>
        </Toolbar>
      </AppBar>

      <Container
        component="main"
        id="contenido"
        maxWidth="lg"
        className="page-container"
        sx={{ flex: 1, py: { xs: 2, sm: 4 }, px: { xs: 2, sm: 3 } }}
      >
        {children}
      </Container>

      <Box component="footer" sx={{ py: 2, textAlign: 'center' }}>
        <Typography variant="caption" color="text.secondary">
          Convenciones · sesión en memoria · el portal público no usa token
        </Typography>
      </Box>
    </Box>
  );
}
