import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import MenuItem from '@mui/material/MenuItem';
import Pagination from '@mui/material/Pagination';
import Stack from '@mui/material/Stack';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { SECTIONS } from '../../lib/navigation';
import { useCapabilities } from '../auth/useCapabilities';
import { useServices } from '../../services';
import type { AgreementSummary, PublicationState } from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { formatDateTime } from '../../lib/format';
import { addYearsIso, todayIso } from '../../lib/dates';
import { dateOrder, firstError, isoDate, requiredText } from '../../lib/forms';

const PAGE_SIZE = 10;

export function AgreementsPage() {
  const { api } = useServices();
  const { has } = useCapabilities();
  const navigate = useNavigate();
  const [items, setItems] = useState<AgreementSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [state, setState] = useState<PublicationState | ''>('');
  const [error, setError] = useState<UiError | null>(null);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);

    try {
      const response = await api.listAgreements({
        page,
        pageSize: PAGE_SIZE,
        state: state === '' ? null : state,
      });
      setItems(response.items);
      setTotal(response.total);
    } catch (failure) {
      setError(describeError(failure));
      setItems([]);
      setTotal(0);
    } finally {
      setLoading(false);
    }
  }, [api, page, state]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Box>
      <PageHeader
        title={SECTIONS.agreements.label}
        subtitle={SECTIONS.agreements.help}
        actions={
          has('agreement.write') ? (
            <Button variant="contained" onClick={() => setCreating(true)}>
              Nueva convención
            </Button>
          ) : null
        }
      />

      {error ? <Alert severity="error">{error.detail}</Alert> : null}

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 2 }}>
        <TextField
          select
          size="small"
          label="Estado"
          value={state}
          onChange={(event) => {
            setPage(1);
            setState(event.target.value as PublicationState | '');
          }}
          sx={{ minWidth: 180 }}
        >
          <MenuItem value="">Todos</MenuItem>
          <MenuItem value="Draft">Borrador</MenuItem>
          <MenuItem value="Published">Publicado</MenuItem>
        </TextField>
      </Stack>

      {loading ? (
        <Typography variant="body2" color="text.secondary">
          Cargando convenciones…
        </Typography>
      ) : items.length === 0 ? (
        <EmptyState message="No hay convenciones que coincidan con el filtro." />
      ) : (
        <Table size="small" aria-label="Convenciones">
          <TableHead>
            <TableRow>
              <TableCell>Título</TableCell>
              <TableCell>Empresa</TableCell>
              <TableCell>Vigencia</TableCell>
              <TableCell>Estado</TableCell>
              <TableCell align="right">Cláusulas</TableCell>
              <TableCell />
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((agreement) => (
              <TableRow key={agreement.id}>
                <TableCell>{agreement.title}</TableCell>
                <TableCell>{agreement.companyName}</TableCell>
                <TableCell>
                  {agreement.validFrom} – {agreement.validTo ?? 'sin fin'}
                </TableCell>
                <TableCell>
                  {agreement.state === 'Published'
                    ? `Publicado ${agreement.publishedAt ? formatDateTime(agreement.publishedAt) : ''}`
                    : 'Borrador'}
                </TableCell>
                <TableCell align="right">{agreement.clauseCount}</TableCell>
                <TableCell align="right">
                  <Button
                    component={RouterLink}
                    to={`/agreements/${agreement.id}`}
                    size="small"
                    aria-label={`Abrir ${agreement.title}`}
                  >
                    Abrir
                  </Button>
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
          aria-label="Paginación de convenciones"
        />
      </Box>

      <CreateAgreementDialog
        open={creating}
        onClose={() => setCreating(false)}
        onCreate={async (input) => {
          const created = await api.createAgreement(input);
          setCreating(false);
          navigate(`/agreements/${created.id}`);
        }}
      />
    </Box>
  );
}

function CreateAgreementDialog({
  open,
  onClose,
  onCreate,
}: {
  open: boolean;
  onClose: () => void;
  onCreate: (input: { validFrom: string; validTo: string | null; scope: string }) => Promise<void>;
}) {
  const [validFrom, setValidFrom] = useState(todayIso());
  const [validTo, setValidTo] = useState(addYearsIso(todayIso(), 2));
  const [scope, setScope] = useState('Nacional');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const submit = async () => {
    const validation = firstError([
      isoDate(validFrom),
      dateOrder(validFrom, validTo),
      requiredText(scope, 'Indique el ámbito de la convención.'),
    ]);

    if (validation) {
      setError(validation);
      return;
    }

    setSubmitting(true);
    setError(null);

    try {
      await onCreate({ validFrom, validTo: validTo === '' ? null : validTo, scope: scope.trim() });
    } catch (failure) {
      setError(describeError(failure).detail);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="create-agreement">
      <DialogTitle id="create-agreement">Nueva convención</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <TextField
            label="Vigencia desde"
            type="date"
            value={validFrom}
            onChange={(event) => setValidFrom(event.target.value)}
            InputLabelProps={{ shrink: true }}
            required
          />
          <TextField
            label="Vigencia hasta"
            type="date"
            value={validTo}
            onChange={(event) => setValidTo(event.target.value)}
            InputLabelProps={{ shrink: true }}
            helperText="Opcional. Puede dejarla vacía para una convención sin fecha final."
          />
          <TextField
            label="Ámbito"
            value={scope}
            onChange={(event) => setScope(event.target.value)}
            required
            helperText="Por ejemplo: Nacional, Sectorial, Empresa."
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button variant="contained" onClick={() => void submit()} disabled={submitting}>
          Crear convención
        </Button>
      </DialogActions>
    </Dialog>
  );
}
