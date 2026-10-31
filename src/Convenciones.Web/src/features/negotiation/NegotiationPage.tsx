import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
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
import { Link as RouterLink, useParams } from 'react-router-dom';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { useCapabilities } from '../auth/useCapabilities';
import { useServices } from '../../services';
import type {
  CaseState,
  CaseTransition,
  NegotiationCaseDetail,
  NegotiationCaseSummary,
} from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { formatDateTime } from '../../lib/format';
import { addYearsIso, todayIso } from '../../lib/dates';
import { firstError, isoDate, positiveInteger, requiredText } from '../../lib/forms';

const PAGE_SIZE = 10;

const STATE_LABEL: Record<CaseState, string> = {
  Open: 'Abierto',
  InProgress: 'En curso',
  Pending: 'Pendiente',
  Agreed: 'Acordado',
  Closed: 'Cerrado',
};

const TRANSITIONS: Record<CaseState, Array<{ step: CaseTransition; label: string }>> = {
  Open: [{ step: 'in-progress', label: 'Iniciar negociación' }],
  InProgress: [{ step: 'pending', label: 'Pasar a pendiente' }],
  Pending: [
    { step: 'agree', label: 'Marcar como acordado' },
    { step: 'in-progress', label: 'Volver a en curso' },
  ],
  Agreed: [{ step: 'close', label: 'Cerrar caso' }],
  Closed: [{ step: 'reopen', label: 'Reabrir' }],
};

export function NegotiationPage() {
  const { api } = useServices();
  const { has } = useCapabilities();
  const [items, setItems] = useState<NegotiationCaseSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [state, setState] = useState<CaseState | ''>('');
  const [error, setError] = useState<UiError | null>(null);
  const [creating, setCreating] = useState(false);
  const [startedOn, setStartedOn] = useState(todayIso());

  const load = useCallback(async () => {
    try {
      const response = await api.listCases({ page, pageSize: PAGE_SIZE, state: state === '' ? null : state });
      setItems(response.items);
      setTotal(response.total);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, page, state]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Box>
      <PageHeader
        title="Negociación"
        subtitle="Casos por empresa. Las etapas son explícitas: iniciar, pendiente, acordar, cerrar y reabrir."
        actions={
          has('negotiation.write') ? (
            <Button variant="contained" onClick={() => setCreating(true)}>
              Nuevo caso
            </Button>
          ) : null
        }
      />

      {error ? <Alert severity="error">{error.detail}</Alert> : null}

      <TextField
        select
        size="small"
        label="Etapa"
        value={state}
        onChange={(event) => {
          setPage(1);
          setState(event.target.value as CaseState | '');
        }}
        sx={{ minWidth: 200, mb: 2 }}
      >
        <MenuItem value="">Todas</MenuItem>
        {(Object.keys(STATE_LABEL) as CaseState[]).map((key) => (
          <MenuItem key={key} value={key}>
            {STATE_LABEL[key]}
          </MenuItem>
        ))}
      </TextField>

      {items.length === 0 ? (
        <EmptyState message="No hay casos de negociación." />
      ) : (
        <Table size="small" aria-label="Casos de negociación">
          <TableHead>
            <TableRow>
              <TableCell>Inicio</TableCell>
              <TableCell>Etapa</TableCell>
              <TableCell align="right">Peticiones</TableCell>
              <TableCell align="right">Reuniones</TableCell>
              <TableCell align="right">Acuerdos</TableCell>
              <TableCell />
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((negotiationCase) => (
              <TableRow key={negotiationCase.id}>
                <TableCell>{negotiationCase.startedOn}</TableCell>
                <TableCell>
                  <Chip size="small" label={STATE_LABEL[negotiationCase.state]} />
                  {negotiationCase.promotedAgreementId ? <Chip size="small" label="Promovido" sx={{ ml: 1 }} /> : null}
                </TableCell>
                <TableCell align="right">{negotiationCase.petitionCount}</TableCell>
                <TableCell align="right">{negotiationCase.meetingCount}</TableCell>
                <TableCell align="right">{negotiationCase.meetingAgreementCount}</TableCell>
                <TableCell align="right">
                  <Button component={RouterLink} to={`/negotiation/${negotiationCase.id}`} size="small">
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
          aria-label="Paginación de casos"
        />
      </Box>

      <Dialog open={creating} onClose={() => setCreating(false)} aria-labelledby="create-case">
        <DialogTitle id="create-case">Nuevo caso</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <TextField
              label="Fecha de inicio"
              type="date"
              value={startedOn}
              onChange={(event) => setStartedOn(event.target.value)}
              InputLabelProps={{ shrink: true }}
              required
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setCreating(false)}>Cancelar</Button>
          <Button
            variant="contained"
            onClick={() => {
              if (isoDate(startedOn) !== null) {
                return;
              }

              void api
                .createCase(startedOn)
                .then(() => {
                  setCreating(false);
                  void load();
                })
                .catch((failure: unknown) => setError(describeError(failure)));
            }}
          >
            Crear
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

export function NegotiationCasePage() {
  const { caseId = '' } = useParams();
  const { api } = useServices();
  const { has } = useCapabilities();
  const [negotiationCase, setNegotiationCase] = useState<NegotiationCaseDetail | null>(null);
  const [error, setError] = useState<UiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [petitionOpen, setPetitionOpen] = useState(false);
  const [meetingOpen, setMeetingOpen] = useState(false);
  const [agreementOpen, setAgreementOpen] = useState(false);
  const [promoteOpen, setPromoteOpen] = useState(false);

  const load = useCallback(async () => {
    try {
      setNegotiationCase(await api.getCase(caseId));
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error && !negotiationCase) {
    return <Alert severity="error">{error.detail}</Alert>;
  }

  if (!negotiationCase) {
    return <Typography variant="body2">Cargando caso…</Typography>;
  }

  const canWrite = has('negotiation.write');
  const closed = negotiationCase.state === 'Closed';
  const promoted = negotiationCase.promotedAgreementId !== null;
  const agreements = negotiationCase.petitions.flatMap((petition) => petition.agreements);

  const transition = async (step: CaseTransition) => {
    setError(null);

    try {
      setNegotiationCase(await api.transitionCase(negotiationCase.id, step));
      setNotice('Etapa actualizada.');
    } catch (failure) {
      setError(describeError(failure));
    }
  };

  return (
    <Box>
      <PageHeader
        title={`Caso iniciado el ${negotiationCase.startedOn}`}
        subtitle={
          <Stack direction="row" spacing={1} alignItems="center">
            <Chip size="small" label={STATE_LABEL[negotiationCase.state]} />
            {promoted ? <Chip size="small" color="success" label="Promovido a acuerdo" /> : null}
            {negotiationCase.closedAt ? (
              <Typography variant="caption" color="text.secondary">
                Cerrado {formatDateTime(negotiationCase.closedAt)}
              </Typography>
            ) : null}
          </Stack>
        }
        actions={
          <Stack direction="row" spacing={1} flexWrap="wrap">
            <Button component={RouterLink} to="/negotiation" size="small">
              Volver
            </Button>
            {canWrite
              ? TRANSITIONS[negotiationCase.state]
                  .filter((item) => !(item.step === 'reopen' && promoted))
                  .map((item) => (
                    <Button key={item.step} size="small" onClick={() => void transition(item.step)}>
                      {item.label}
                    </Button>
                  ))
              : null}
            {has('negotiation.promote') && closed && !promoted ? (
              <Button size="small" variant="contained" onClick={() => setPromoteOpen(true)}>
                Promover a acuerdo
              </Button>
            ) : null}
          </Stack>
        }
      />

      {notice ? <Alert severity="success" sx={{ mb: 2 }} onClose={() => setNotice(null)}>{notice}</Alert> : null}
      {error ? <Alert severity="error" sx={{ mb: 2 }}>{error.detail}</Alert> : null}

      {negotiationCase.state === 'Agreed' && agreements.length === 0 ? (
        <Alert severity="info" sx={{ mb: 2 }}>
          Para cerrar el caso hace falta al menos un acuerdo de reunión registrado.
        </Alert>
      ) : null}

      <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr', md: 'repeat(2, 1fr)' } }}>
        <Paper sx={{ p: 2 }} aria-label="Peticiones">
          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
            <Typography variant="h6" component="h2">
              Peticiones ({negotiationCase.petitions.length})
            </Typography>
            {canWrite && !closed ? (
              <Button size="small" onClick={() => setPetitionOpen(true)}>
                Añadir
              </Button>
            ) : null}
          </Box>
          {negotiationCase.petitions.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              Sin peticiones.
            </Typography>
          ) : (
            <Stack spacing={1.5}>
              {negotiationCase.petitions.map((petition) => (
                <Box key={petition.id}>
                  <Typography variant="subtitle2" component="p">
                    {petition.number}. {petition.title} · {petition.state}
                  </Typography>
                  <Typography variant="caption" color="text.secondary" component="div">
                    {petition.body}
                  </Typography>
                </Box>
              ))}
            </Stack>
          )}
        </Paper>

        <Paper sx={{ p: 2 }} aria-label="Reuniones y acuerdos">
          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
            <Typography variant="h6" component="h2">
              Reuniones ({negotiationCase.meetings.length})
            </Typography>
            {canWrite && !closed ? (
              <Button size="small" onClick={() => setMeetingOpen(true)}>
                Añadir
              </Button>
            ) : null}
          </Box>
          {negotiationCase.meetings.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              Sin reuniones.
            </Typography>
          ) : (
            <Stack spacing={1}>
              {negotiationCase.meetings.map((meeting) => (
                <Typography key={meeting.id} variant="body2" component="p">
                  {meeting.heldOn} {meeting.summary ? `· ${meeting.summary}` : ''}
                </Typography>
              ))}
            </Stack>
          )}

          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mt: 2 }}>
            <Typography variant="h6" component="h2">
              Acuerdos de reunión ({agreements.length})
            </Typography>
            {canWrite && !closed && negotiationCase.petitions.length > 0 && negotiationCase.meetings.length > 0 ? (
              <Button size="small" onClick={() => setAgreementOpen(true)}>
                Registrar
              </Button>
            ) : null}
          </Box>
          {agreements.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              Sin acuerdos.
            </Typography>
          ) : (
            <Stack spacing={1}>
              {agreements.map((agreement) => (
                <Typography key={agreement.id} variant="body2" component="p">
                  {agreement.number}. {agreement.title} ({agreement.agreedOn})
                </Typography>
              ))}
            </Stack>
          )}
        </Paper>
      </Box>

      <PetitionDialog
        open={petitionOpen}
        nextNumber={negotiationCase.petitions.length + 1}
        onClose={() => setPetitionOpen(false)}
        onSubmit={async (input) => {
          await api.addPetition(negotiationCase.id, input);
          setPetitionOpen(false);
          await load();
        }}
      />

      <MeetingDialog
        open={meetingOpen}
        onClose={() => setMeetingOpen(false)}
        onSubmit={async (input) => {
          await api.addMeeting(negotiationCase.id, input);
          setMeetingOpen(false);
          await load();
        }}
      />

      <AgreementDialog
        open={agreementOpen}
        negotiationCase={negotiationCase}
        onClose={() => setAgreementOpen(false)}
        onSubmit={async (petitionId, input) => {
          await api.addMeetingAgreement(negotiationCase.id, petitionId, input);
          setAgreementOpen(false);
          await load();
        }}
      />

      <PromoteDialog
        open={promoteOpen}
        onClose={() => setPromoteOpen(false)}
        onSubmit={async (input) => {
          await api.promoteCase(negotiationCase.id, input);
          setPromoteOpen(false);
          await load();
        }}
      />
    </Box>
  );
}

function PetitionDialog({
  open,
  nextNumber,
  onClose,
  onSubmit,
}: {
  open: boolean;
  nextNumber: number;
  onClose: () => void;
  onSubmit: (input: { number: number; title: string; body: string }) => Promise<void>;
}) {
  const [number, setNumber] = useState(String(nextNumber));
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="petition-dialog">
      <DialogTitle id="petition-dialog">Nueva petición</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <TextField label="Número" value={number} onChange={(event) => setNumber(event.target.value)} required />
          <TextField label="Título" value={title} onChange={(event) => setTitle(event.target.value)} required />
          <TextField
            label="Texto"
            value={body}
            onChange={(event) => setBody(event.target.value)}
            required
            multiline
            minRows={4}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          onClick={() => {
            const validation = firstError([
              positiveInteger(number),
              requiredText(title, 'Indique el título.'),
              requiredText(body, 'Indique el texto.'),
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSubmit({ number: Number(number), title: title.trim(), body }).catch((failure: unknown) =>
              setError(describeError(failure).detail),
            );
          }}
        >
          Crear
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function MeetingDialog({
  open,
  onClose,
  onSubmit,
}: {
  open: boolean;
  onClose: () => void;
  onSubmit: (input: { heldOn: string; summary: string | null; attendees: string | null }) => Promise<void>;
}) {
  const [heldOn, setHeldOn] = useState(todayIso());
  const [summary, setSummary] = useState('');
  const [attendees, setAttendees] = useState('');
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="meeting-dialog">
      <DialogTitle id="meeting-dialog">Nueva reunión</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <TextField
            label="Fecha"
            type="date"
            value={heldOn}
            onChange={(event) => setHeldOn(event.target.value)}
            InputLabelProps={{ shrink: true }}
            required
          />
          <TextField label="Resumen" value={summary} onChange={(event) => setSummary(event.target.value)} />
          <TextField label="Asistentes" value={attendees} onChange={(event) => setAttendees(event.target.value)} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          onClick={() => {
            if (isoDate(heldOn) !== null) {
              setError('Indique una fecha válida.');
              return;
            }

            void onSubmit({
              heldOn,
              summary: summary.trim() === '' ? null : summary.trim(),
              attendees: attendees.trim() === '' ? null : attendees.trim(),
            }).catch((failure: unknown) => setError(describeError(failure).detail));
          }}
        >
          Crear
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function AgreementDialog({
  open,
  negotiationCase,
  onClose,
  onSubmit,
}: {
  open: boolean;
  negotiationCase: NegotiationCaseDetail;
  onClose: () => void;
  onSubmit: (
    petitionId: string,
    input: { meetingId: string; agreedOn: string; number: number; title: string; body: string },
  ) => Promise<void>;
}) {
  const [petitionId, setPetitionId] = useState(negotiationCase.petitions[0]?.id ?? '');
  const [meetingId, setMeetingId] = useState(negotiationCase.meetings[0]?.id ?? '');
  const [agreedOn, setAgreedOn] = useState(todayIso());
  const [number, setNumber] = useState('1');
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="agreement-dialog">
      <DialogTitle id="agreement-dialog">Registrar acuerdo de reunión</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <TextField select label="Petición" value={petitionId} onChange={(event) => setPetitionId(event.target.value)}>
            {negotiationCase.petitions.map((petition) => (
              <MenuItem key={petition.id} value={petition.id}>
                {petition.number}. {petition.title}
              </MenuItem>
            ))}
          </TextField>
          <TextField select label="Reunión" value={meetingId} onChange={(event) => setMeetingId(event.target.value)}>
            {negotiationCase.meetings.map((meeting) => (
              <MenuItem key={meeting.id} value={meeting.id}>
                {meeting.heldOn}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            label="Fecha del acuerdo"
            type="date"
            value={agreedOn}
            onChange={(event) => setAgreedOn(event.target.value)}
            InputLabelProps={{ shrink: true }}
            required
          />
          <TextField label="Número" value={number} onChange={(event) => setNumber(event.target.value)} required />
          <TextField label="Título" value={title} onChange={(event) => setTitle(event.target.value)} required />
          <TextField
            label="Texto"
            value={body}
            onChange={(event) => setBody(event.target.value)}
            required
            multiline
            minRows={3}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          onClick={() => {
            const validation = firstError([
              isoDate(agreedOn),
              positiveInteger(number),
              requiredText(title, 'Indique el título.'),
              requiredText(body, 'Indique el texto.'),
              petitionId === '' ? 'Seleccione una petición.' : null,
              meetingId === '' ? 'Seleccione una reunión.' : null,
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSubmit(petitionId, {
              meetingId,
              agreedOn,
              number: Number(number),
              title: title.trim(),
              body,
            }).catch((failure: unknown) => setError(describeError(failure).detail));
          }}
        >
          Registrar
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function PromoteDialog({
  open,
  onClose,
  onSubmit,
}: {
  open: boolean;
  onClose: () => void;
  onSubmit: (input: { validFrom: string; validTo: string | null; scope: string }) => Promise<void>;
}) {
  const [validFrom, setValidFrom] = useState(todayIso());
  const [validTo, setValidTo] = useState(addYearsIso(todayIso(), 2));
  const [scope, setScope] = useState('Nacional');
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="promote-dialog">
      <DialogTitle id="promote-dialog">Promover a acuerdo</DialogTitle>
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
          />
          <TextField label="Ámbito" value={scope} onChange={(event) => setScope(event.target.value)} required />
          <Typography variant="caption" color="text.secondary">
            La promoción crea la cabecera del acuerdo. Las cláusulas se redactan después, en el acuerdo.
          </Typography>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          onClick={() => {
            const validation = firstError([
              isoDate(validFrom),
              requiredText(scope, 'Indique el ámbito.'),
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSubmit({ validFrom, validTo: validTo === '' ? null : validTo, scope: scope.trim() }).catch(
              (failure: unknown) => setError(describeError(failure).detail),
            );
          }}
        >
          Promover
        </Button>
      </DialogActions>
    </Dialog>
  );
}
