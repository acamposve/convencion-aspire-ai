import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Divider from '@mui/material/Divider';
import FormControlLabel from '@mui/material/FormControlLabel';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { useCapabilities } from '../auth/useCapabilities';
import { useServices } from '../../services';
import type { AgreementDetail, ClauseInput, ClauseView, HeadingView } from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { formatDateTime } from '../../lib/format';
import { dateOrder, firstError, isoDate, positiveInteger, requiredText } from '../../lib/forms';

export function AgreementEditorPage() {
  const { id = '' } = useParams();
  const { api } = useServices();
  const { has } = useCapabilities();
  const [agreement, setAgreement] = useState<AgreementDetail | null>(null);
  const [headings, setHeadings] = useState<HeadingView[]>([]);
  const [error, setError] = useState<UiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<'publish' | 'unpublish' | null>(null);
  const [editingHeader, setEditingHeader] = useState(false);
  const [editingClause, setEditingClause] = useState<ClauseView | 'new' | null>(null);

  const load = useCallback(async () => {
    setError(null);

    try {
      const [detail, headingList] = await Promise.all([api.getAgreement(id), api.listHeadings()]);
      setAgreement(detail);
      setHeadings(headingList);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, id]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error && !agreement) {
    return <Alert severity="error">{error.detail}</Alert>;
  }

  if (!agreement) {
    return <Typography variant="body2">Cargando acuerdo…</Typography>;
  }

  const published = agreement.state === 'Published';
  const canWrite = has('agreement.write') && !published;
  const canPublish = has('agreement.publish');
  const clauses = [...agreement.clauses].sort((left, right) => left.number - right.number);
  const nextNumber = clauses.length === 0 ? 1 : Math.max(...clauses.map((c) => c.number)) + 1;

  const run = async (action: () => Promise<AgreementDetail>) => {
    setError(null);
    setNotice(null);

    try {
      const updated = await action();
      setAgreement(updated);
      return updated;
    } catch (failure) {
      setError(describeError(failure));
      return null;
    }
  };

  return (
    <Box>
      <PageHeader
        title={agreement.title}
        subtitle={`${agreement.companyName} · vigencia ${agreement.validFrom} – ${agreement.validTo ?? 'sin fin'} · ámbito ${agreement.scope}`}
        actions={
          <Stack direction="row" spacing={1} flexWrap="wrap">
            <Button component={RouterLink} to="/agreements" size="small">
              Volver
            </Button>
            {canWrite ? (
              <Button size="small" onClick={() => setEditingHeader(true)}>
                Editar cabecera
              </Button>
            ) : null}
            {canPublish && published ? (
              <Button size="small" color="warning" onClick={() => setConfirm('unpublish')}>
                Despublicar
              </Button>
            ) : null}
            {canPublish && !published ? (
              <Button size="small" variant="contained" onClick={() => setConfirm('publish')}>
                Publicar
              </Button>
            ) : null}
          </Stack>
        }
      />

      {notice ? (
        <Alert severity="success" sx={{ mb: 2 }} onClose={() => setNotice(null)}>
          {notice}
        </Alert>
      ) : null}
      {error ? (
        <Alert severity="error" sx={{ mb: 2 }}>
          {error.detail}
        </Alert>
      ) : null}

      {published ? (
        <Alert severity="info" sx={{ mb: 2 }}>
          Este acuerdo está publicado desde {agreement.publishedAt ? formatDateTime(agreement.publishedAt) : '—'} y
          no se edita en sitio. Para modificar su contenido hay que despublicarlo de forma explícita.
        </Alert>
      ) : null}

      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
        <Typography variant="h6" component="h2">
          Cláusulas ({clauses.length})
        </Typography>
        {canWrite ? (
          <Button variant="outlined" size="small" onClick={() => setEditingClause('new')}>
            Añadir cláusula
          </Button>
        ) : null}
      </Box>

      {clauses.length === 0 ? (
        <EmptyState message="Este acuerdo todavía no tiene cláusulas. Un acuerdo sin cláusulas no se puede publicar." />
      ) : (
        <List aria-label="Cláusulas del acuerdo" dense>
          {clauses.map((clause) => (
            <Box key={clause.id} component="li" sx={{ listStyle: 'none' }}>
              <ListItem
                alignItems="flex-start"
                secondaryAction={
                  canWrite ? (
                    <Stack direction="row" spacing={0.5}>
                      <Button size="small" onClick={() => setEditingClause(clause)} aria-label={`Editar cláusula ${clause.number}`}>
                        Editar
                      </Button>
                      <Button
                        size="small"
                        color="error"
                        aria-label={`Eliminar cláusula ${clause.number}`}
                        onClick={() => {
                          void (async () => {
                            try {
                              await api.deleteClause(agreement.id, clause.id, clause.rowVersion);
                              setNotice(`Cláusula ${clause.number} eliminada.`);
                              await load();
                            } catch (failure) {
                              setError(describeError(failure));
                            }
                          })();
                        }}
                      >
                        Eliminar
                      </Button>
                    </Stack>
                  ) : null
                }
              >
                <Box sx={{ pr: 16 }}>
                  <Typography variant="subtitle1" component="p" sx={{ fontWeight: 600 }}>
                    {clause.number}. {clause.title}
                    {clause.isExcludedFromCompilation ? (
                      <Chip size="small" label="No se compila" sx={{ ml: 1 }} />
                    ) : null}
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    {clause.headingName ?? 'Sin materia'}
                    {clause.summary ? ` · ${clause.summary}` : ''}
                  </Typography>
                  <Typography
                    variant="body2"
                    component="div"
                    sx={{ mt: 0.5 }}
                    dangerouslySetInnerHTML={{ __html: clause.body }}
                  />
                </Box>
              </ListItem>
              <Divider />
            </Box>
          ))}
        </List>
      )}

      <ConfirmDialog
        open={confirm !== null}
        title={confirm === 'publish' ? 'Publicar acuerdo' : 'Despublicar acuerdo'}
        description={
          confirm === 'publish'
            ? 'El acuerdo pasará a ser visible en el portal público y no podrá editarse hasta despublicarlo. ¿Continuar?'
            : 'El acuerdo dejará de ser visible en el portal público y volverá a borrador. ¿Continuar?'
        }
        confirmLabel={confirm === 'publish' ? 'Publicar' : 'Despublicar'}
        onCancel={() => setConfirm(null)}
        onConfirm={() => {
          const action = confirm;
          setConfirm(null);

          void (async () => {
            if (action === 'publish') {
              const updated = await run(() => api.publishAgreement(agreement.id));
              if (updated) {
                setNotice('Acuerdo publicado.');
              }
            } else {
              const updated = await run(() => api.unpublishAgreement(agreement.id));
              if (updated) {
                setNotice('Acuerdo despublicado; ahora puede editarse.');
              }
            }
          })();
        }}
      />

      <HeaderDialog
        open={editingHeader}
        agreement={agreement}
        onClose={() => setEditingHeader(false)}
        onSave={async (input) => {
          const updated = await run(() => api.updateAgreement(agreement.id, input, agreement.rowVersion));
          if (updated) {
            setEditingHeader(false);
            setNotice('Cabecera actualizada.');
          }
        }}
      />

      <ClauseDialog
        key={editingClause === null ? 'closed' : editingClause === 'new' ? 'new' : editingClause.id}
        open={editingClause !== null}
        clause={editingClause === 'new' ? null : editingClause}
        headings={headings}
        defaultNumber={nextNumber}
        onClose={() => setEditingClause(null)}
        onSubmit={async (input) => {
          if (editingClause === 'new') {
            await api.createClause(agreement.id, input);
            setNotice('Cláusula creada.');
          } else if (editingClause) {
            await api.updateClause(agreement.id, editingClause.id, input, editingClause.rowVersion);
            setNotice('Cláusula actualizada.');
          }

          setEditingClause(null);
          await load();
        }}
      />
    </Box>
  );
}

function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel,
  onCancel,
  onConfirm,
}: {
  open: boolean;
  title: string;
  description: string;
  confirmLabel: string;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Dialog open={open} onClose={onCancel} aria-labelledby="confirm-title" aria-describedby="confirm-description">
      <DialogTitle id="confirm-title">{title}</DialogTitle>
      <DialogContent>
        <DialogContentText id="confirm-description">{description}</DialogContentText>
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel}>Cancelar</Button>
        <Button variant="contained" onClick={onConfirm}>
          {confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function HeaderDialog({
  open,
  agreement,
  onClose,
  onSave,
}: {
  open: boolean;
  agreement: AgreementDetail;
  onClose: () => void;
  onSave: (input: { validFrom: string; validTo: string | null; scope: string }) => Promise<void>;
}) {
  const [validFrom, setValidFrom] = useState(agreement.validFrom);
  const [validTo, setValidTo] = useState(agreement.validTo ?? '');
  const [scope, setScope] = useState(agreement.scope);
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="header-dialog">
      <DialogTitle id="header-dialog">Editar cabecera</DialogTitle>
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
          <TextField
            label="Ámbito"
            value={scope}
            onChange={(event) => setScope(event.target.value)}
            required
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          onClick={() => {
            const validation = firstError([
              isoDate(validFrom),
              dateOrder(validFrom, validTo),
              requiredText(scope, 'Indique el ámbito del acuerdo.'),
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSave({ validFrom, validTo: validTo === '' ? null : validTo, scope: scope.trim() });
          }}
        >
          Guardar
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function ClauseDialog({
  open,
  clause,
  headings,
  defaultNumber,
  onClose,
  onSubmit,
}: {
  open: boolean;
  clause: ClauseView | null;
  headings: HeadingView[];
  defaultNumber: number;
  onClose: () => void;
  onSubmit: (input: ClauseInput) => Promise<void>;
}) {
  const [number, setNumber] = useState(String(clause?.number ?? defaultNumber));
  const [title, setTitle] = useState(clause?.title ?? '');
  const [summary, setSummary] = useState(clause?.summary ?? '');
  const [body, setBody] = useState(clause?.body ?? '');
  const [headingId, setHeadingId] = useState(clause?.headingId ?? '');
  const [note, setNote] = useState(clause?.economicComparisonNote ?? '');
  const [excluded, setExcluded] = useState(clause?.isExcludedFromCompilation ?? false);
  const [exclusionNote, setExclusionNote] = useState(clause?.exclusionNote ?? '');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md" aria-labelledby="clause-dialog">
      <DialogTitle id="clause-dialog">{clause ? `Cláusula ${clause.number}` : 'Nueva cláusula'}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <TextField
              label="Número"
              value={number}
              onChange={(event) => setNumber(event.target.value)}
              required
              sx={{ width: { xs: '100%', sm: 160 } }}
            />
            <TextField
              select
              label="Materia"
              value={headingId}
              onChange={(event) => setHeadingId(event.target.value)}
              required
              sx={{ flex: 1 }}
              helperText="Obligatoria: sin materia la cláusula no entra en la comparación."
            >
              {headings.map((heading) => (
                <MenuItem key={heading.id} value={heading.id}>
                  {heading.groupName} · {heading.name}
                </MenuItem>
              ))}
            </TextField>
          </Stack>
          <TextField
            label="Título"
            value={title}
            onChange={(event) => setTitle(event.target.value)}
            required
          />
          <TextField label="Resumen" value={summary} onChange={(event) => setSummary(event.target.value)} />
          <TextField
            label="Texto"
            value={body}
            onChange={(event) => setBody(event.target.value)}
            required
            multiline
            minRows={5}
            helperText="Acepta formato básico (negrita, párrafos, listas). El servidor elimina scripts."
          />
          <TextField
            label="Nota económica"
            value={note}
            onChange={(event) => setNote(event.target.value)}
            helperText="Opcional. Se muestra junto a la materia en la comparación."
          />
          <FormControlLabel
            control={
              <Switch checked={excluded} onChange={(event) => setExcluded(event.target.checked)} />
            }
            label="Excluida de la compilación"
          />
          {excluded ? (
            <TextField
              label="Motivo de la exclusión"
              value={exclusionNote}
              onChange={(event) => setExclusionNote(event.target.value)}
            />
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancelar</Button>
        <Button
          variant="contained"
          disabled={submitting}
          onClick={() => {
            const validation = firstError([
              positiveInteger(number),
              requiredText(title, 'Indique el título de la cláusula.'),
              requiredText(body, 'Indique el texto de la cláusula.'),
              headingId === '' ? 'Seleccione una materia.' : null,
              excluded ? requiredText(exclusionNote, 'Indique el motivo de la exclusión.') : null,
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            setSubmitting(true);
            setError(null);

            void onSubmit({
              number: Number(number),
              title: title.trim(),
              summary: summary.trim() === '' ? null : summary.trim(),
              body,
              headingId,
              economicComparisonNote: note.trim() === '' ? null : note.trim(),
              isExcludedFromCompilation: excluded,
              exclusionNote: excluded && exclusionNote.trim() !== '' ? exclusionNote.trim() : null,
            }).finally(() => setSubmitting(false));
          }}
        >
          Guardar
        </Button>
      </DialogActions>
    </Dialog>
  );
}
