import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import FormControlLabel from '@mui/material/FormControlLabel';
import ListItemText from '@mui/material/ListItemText';
import MenuItem from '@mui/material/MenuItem';
import Pagination from '@mui/material/Pagination';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useState } from 'react';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { useServices } from '../../services';
import type {
  AuditEntryView,
  CapabilityView,
  RoleView,
  UserSummary,
} from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { formatDateTime } from '../../lib/format';
import { firstError, requiredText } from '../../lib/forms';

const PAGE_SIZE = 10;

export function UsersPage() {
  const { api } = useServices();
  const [items, setItems] = useState<UserSummary[]>([]);
  const [roles, setRoles] = useState<RoleView[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [term, setTerm] = useState('');
  const [error, setError] = useState<UiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [editingRoles, setEditingRoles] = useState<UserSummary | null>(null);

  const load = useCallback(async () => {
    try {
      const [list, roleList] = await Promise.all([
        api.listUsers({ page, pageSize: PAGE_SIZE, q: term === '' ? null : term }),
        api.listRoles(),
      ]);
      setItems(list.items);
      setTotal(list.total);
      setRoles(roleList);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, page, term]);

  useEffect(() => {
    void load();
  }, [load]);

  const toggleBlocked = async (user: UserSummary) => {
    setError(null);

    try {
      await api.updateUser(user.id, { isBlocked: !user.isBlocked });
      setNotice(user.isBlocked ? 'Usuario desbloqueado.' : 'Usuario bloqueado; su token deja de valer en la próxima petición.');
      await load();
    } catch (failure) {
      setError(describeError(failure));
    }
  };

  return (
    <Box>
      <PageHeader
        title="Usuarios"
        subtitle="Altas, bloqueo y roles. El bloqueo surte efecto en la siguiente petición, sin esperar a que caduque el token."
        actions={
          <Button variant="contained" onClick={() => setCreating(true)}>
            Nuevo usuario
          </Button>
        }
      />

      {notice ? <Alert severity="success" sx={{ mb: 2 }} onClose={() => setNotice(null)}>{notice}</Alert> : null}
      {error ? <Alert severity="error" sx={{ mb: 2 }}>{error.detail}</Alert> : null}

      <TextField
        size="small"
        label="Buscar por correo o nombre"
        value={term}
        onChange={(event) => {
          setPage(1);
          setTerm(event.target.value);
        }}
        sx={{ minWidth: 280, mb: 2 }}
      />

      {items.length === 0 ? (
        <EmptyState message="No hay usuarios que coincidan." />
      ) : (
        <Table size="small" aria-label="Usuarios">
          <TableHead>
            <TableRow>
              <TableCell>Nombre</TableCell>
              <TableCell>Correo</TableCell>
              <TableCell>Roles</TableCell>
              <TableCell>Estado</TableCell>
              <TableCell />
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((user) => (
              <TableRow key={user.id}>
                <TableCell>{user.displayName}</TableCell>
                <TableCell>{user.email}</TableCell>
                <TableCell>
                  <Stack direction="row" gap={0.5} flexWrap="wrap">
                    {user.roles.map((role) => (
                      <Chip key={role} size="small" label={role} />
                    ))}
                  </Stack>
                </TableCell>
                <TableCell>
                  {user.isBlocked ? <Chip size="small" color="warning" label="Bloqueado" /> : 'Activo'}
                  {user.expiresOn ? ` · vence ${user.expiresOn}` : ''}
                </TableCell>
                <TableCell align="right">
                  <Stack direction="row" spacing={0.5} justifyContent="flex-end">
                    <Button size="small" onClick={() => setEditingRoles(user)}>
                      Roles
                    </Button>
                    <Button size="small" color={user.isBlocked ? 'primary' : 'warning'} onClick={() => void toggleBlocked(user)}>
                      {user.isBlocked ? 'Desbloquear' : 'Bloquear'}
                    </Button>
                  </Stack>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <Box sx={{ display: 'flex', justifyContent: 'center', mt: 3 }}>
        <Pagination
          count={Math.max(1, Math.ceil(total / PAGE_SIZE))}
          page={page}
          onChange={(_, value) => setPage(value)}
          aria-label="Paginación de usuarios"
        />
      </Box>

      <CreateUserDialog
        open={creating}
        roles={roles}
        onClose={() => setCreating(false)}
        onSubmit={async (input) => {
          await api.createUser(input);
          setCreating(false);
          setNotice('Usuario creado.');
          await load();
        }}
      />

      <RolesDialog
        open={editingRoles !== null}
        user={editingRoles}
        roles={roles}
        onClose={() => setEditingRoles(null)}
        onSubmit={async (user, roleKeys) => {
          try {
            await api.replaceUserRoles(user.id, roleKeys);
            setEditingRoles(null);
            setNotice('Roles actualizados.');
            await load();
          } catch (failure) {
            setError(describeError(failure));
          }
        }}
      />
    </Box>
  );
}

function CreateUserDialog({
  open,
  roles,
  onClose,
  onSubmit,
}: {
  open: boolean;
  roles: RoleView[];
  onClose: () => void;
  onSubmit: (input: {
    subject: string;
    email: string;
    displayName: string;
    roleKeys: string[];
  }) => Promise<void>;
}) {
  const [subject, setSubject] = useState('');
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [roleKeys, setRoleKeys] = useState<string[]>(['editor']);
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="create-user">
      <DialogTitle id="create-user">Nuevo usuario</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <Typography variant="caption" color="text.secondary">
            El identificador es el sujeto de Supabase Auth. Esta aplicación no crea credenciales ni contraseñas.
          </Typography>
          <TextField
            label="Sujeto (UUID de Supabase)"
            value={subject}
            onChange={(event) => setSubject(event.target.value)}
            required
            helperText="El id del usuario en Supabase Auth, no su correo."
          />
          <TextField label="Correo" type="email" value={email} onChange={(event) => setEmail(event.target.value)} required />
          <TextField
            label="Nombre"
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
            required
          />
          <TextField select label="Roles" value={roleKeys} onChange={(event) => setRoleKeys([event.target.value])} required>
            {roles.map((role) => (
              <MenuItem key={role.key} value={role.key}>
                {role.name}
              </MenuItem>
            ))}
          </TextField>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          onClick={() => {
            const validation = firstError([
              requiredText(subject, 'Indique el sujeto.'),
              requiredText(email, 'Indique el correo.'),
              requiredText(displayName, 'Indique el nombre.'),
              roleKeys.length === 0 ? 'Seleccione al menos un rol.' : null,
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSubmit({ subject: subject.trim(), email: email.trim(), displayName: displayName.trim(), roleKeys }).catch(
              (failure: unknown) => setError(describeError(failure).detail),
            );
          }}
        >
          Crear
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function RolesDialog({
  open,
  user,
  roles,
  onClose,
  onSubmit,
}: {
  open: boolean;
  user: UserSummary | null;
  roles: RoleView[];
  onClose: () => void;
  onSubmit: (user: UserSummary, roleKeys: string[]) => Promise<void>;
}) {
  const [selected, setSelected] = useState<string[]>(user?.roles ?? []);

  useEffect(() => {
    setSelected(user?.roles ?? []);
  }, [user]);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="roles-dialog">
      <DialogTitle id="roles-dialog">Roles de {user?.displayName}</DialogTitle>
      <DialogContent>
        <Stack>
          {roles.map((role) => (
            <FormControlLabel
              key={role.key}
              control={
                <Checkbox
                  checked={selected.includes(role.key)}
                  onChange={(event) => {
                    setSelected((current) =>
                      event.target.checked ? [...current, role.key] : current.filter((key) => key !== role.key),
                    );
                  }}
                />
              }
              label={
                <Box>
                  <ListItemText primary={role.name} secondary={role.capabilities.slice(0, 3).join(', ')} />
                </Box>
              }
            />
          ))}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button variant="contained" onClick={() => user && void onSubmit(user, selected)}>
          Guardar
        </Button>
      </DialogActions>
    </Dialog>
  );
}

export function RolesPage() {
  const { api } = useServices();
  const [roles, setRoles] = useState<RoleView[]>([]);
  const [capabilities, setCapabilities] = useState<CapabilityView[]>([]);
  const [error, setError] = useState<UiError | null>(null);

  useEffect(() => {
    void (async () => {
      try {
        const [roleList, capabilityList] = await Promise.all([api.listRoles(), api.listCapabilities()]);
        setRoles(roleList);
        setCapabilities(capabilityList);
      } catch (failure) {
        setError(describeError(failure));
      }
    })();
  }, [api]);

  return (
    <Box>
      <PageHeader
        title="Roles y capacidades"
        subtitle="La matriz es de solo lectura en esta versión: los roles se definen en la base de datos."
      />

      {error ? <Alert severity="error" sx={{ mb: 2 }}>{error.detail}</Alert> : null}

      <Stack spacing={2}>
        {roles.map((role) => (
          <Paper key={role.id} sx={{ p: 2 }}>
            <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
              <Typography variant="subtitle1" component="h2" sx={{ fontWeight: 600 }}>
                {role.name}
              </Typography>
              <Chip size="small" label={role.key} />
              {role.isSystem ? <Chip size="small" label="Sistema" /> : null}
            </Stack>
            <Stack direction="row" gap={0.5} flexWrap="wrap">
              {role.capabilities.map((capability) => (
                <Chip key={capability} size="small" variant="outlined" label={capability} />
              ))}
            </Stack>
          </Paper>
        ))}
      </Stack>

      <Typography variant="h6" component="h2" sx={{ mt: 4, mb: 1 }}>
        Catálogo de capacidades ({capabilities.length})
      </Typography>
      <Table size="small" aria-label="Capacidades">
        <TableHead>
          <TableRow>
            <TableCell>Capacidad</TableCell>
            <TableCell>Descripción</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {capabilities.map((capability) => (
            <TableRow key={capability.key}>
              <TableCell>{capability.key}</TableCell>
              <TableCell>{capability.description}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Box>
  );
}

export function AuditPage() {
  const { api } = useServices();
  const [items, setItems] = useState<AuditEntryView[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [entityType, setEntityType] = useState('');
  const [module, setModule] = useState('');
  const [error, setError] = useState<UiError | null>(null);

  const load = useCallback(async () => {
    try {
      const response = await api.listAudit({
        page,
        pageSize: PAGE_SIZE,
        entityType: entityType === '' ? null : entityType,
        module: module === '' ? null : module,
      });
      setItems(response.items);
      setTotal(response.total);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, entityType, module, page]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Box>
      <PageHeader
        title="Auditoría"
        subtitle="Registro de solo anexado. Cada fila indica quién, qué y cuándo; el historial no se puede modificar desde la aplicación."
      />

      {error ? <Alert severity="error" sx={{ mb: 2 }}>{error.detail}</Alert> : null}

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 2 }}>
        <TextField
          select
          size="small"
          label="Módulo"
          value={module}
          onChange={(event) => {
            setPage(1);
            setModule(event.target.value);
          }}
          sx={{ minWidth: 180 }}
        >
          <MenuItem value="">Todos</MenuItem>
          {['agreements', 'negotiation', 'publishing', 'identity', 'legal_library', 'documents'].map((value) => (
            <MenuItem key={value} value={value}>
              {value}
            </MenuItem>
          ))}
        </TextField>
        <TextField
          size="small"
          label="Tipo de entidad"
          value={entityType}
          onChange={(event) => {
            setPage(1);
            setEntityType(event.target.value);
          }}
          sx={{ minWidth: 200 }}
        />
      </Stack>

      {items.length === 0 ? (
        <EmptyState message="No hay eventos que coincidan." />
      ) : (
        <Table size="small" aria-label="Auditoría">
          <TableHead>
            <TableRow>
              <TableCell>Cuándo</TableCell>
              <TableCell>Quién</TableCell>
              <TableCell>Módulo</TableCell>
              <TableCell>Entidad</TableCell>
              <TableCell>Acción</TableCell>
              <TableCell>Cambio</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((entry) => (
              <TableRow key={entry.id}>
                <TableCell>{formatDateTime(entry.occurredAt)}</TableCell>
                <TableCell>{entry.actorDisplayName ?? 'sistema'}</TableCell>
                <TableCell>{entry.module}</TableCell>
                <TableCell>
                  {entry.entityType}:{entry.entityId}
                </TableCell>
                <TableCell>{entry.action}</TableCell>
                <TableCell>
                  {entry.fromState || entry.toState
                    ? `${entry.fromState ?? '—'} → ${entry.toState ?? '—'}`
                    : entry.changes
                      ? JSON.stringify(entry.changes)
                      : '—'}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <Box sx={{ display: 'flex', justifyContent: 'center', mt: 3 }}>
        <Pagination
          count={Math.max(1, Math.ceil(total / PAGE_SIZE))}
          page={page}
          onChange={(_, value) => setPage(value)}
          aria-label="Paginación de auditoría"
        />
      </Box>
    </Box>
  );
}
