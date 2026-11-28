import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
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
import { Link as RouterLink, useParams } from 'react-router-dom';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { useCapabilities } from '../auth/useCapabilities';
import { useServices } from '../../services';
import type { HeadingView, StatuteArticleInput, StatuteArticleView, StatuteDetail, StatuteSummary } from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { positiveInteger, firstError, requiredText } from '../../lib/forms';

const PAGE_SIZE = 10;

export function LegalLibraryPage() {
  const { api } = useServices();
  const [items, setItems] = useState<StatuteSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [error, setError] = useState<UiError | null>(null);

  useEffect(() => {
    void (async () => {
      try {
        const response = await api.listStatutes(page, PAGE_SIZE);
        setItems(response.items);
        setTotal(response.total);
      } catch (failure) {
        setError(describeError(failure));
      }
    })();
  }, [api, page]);

  return (
    <Box>
      <PageHeader
        title="Biblioteca legal"
        subtitle="Leyes y artículos. Solo una ley laboral es válida para la comparación; el resto son referencia."
      />

      {error ? <Alert severity="error">{error.detail}</Alert> : null}

      {items.length === 0 ? (
        <EmptyState message="La biblioteca legal está vacía." />
      ) : (
        <Table size="small" aria-label="Leyes">
          <TableHead>
            <TableRow>
              <TableCell>Nombre</TableCell>
              <TableCell>Tipo</TableCell>
              <TableCell>Publicación</TableCell>
              <TableCell align="right">Artículos</TableCell>
              <TableCell />
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((statute) => (
              <TableRow key={statute.id}>
                <TableCell>{statute.name}</TableCell>
                <TableCell>{statute.kind === 'LabourLaw' ? 'Ley laboral' : 'Otra norma'}</TableCell>
                <TableCell>{statute.publishedOn ?? '—'}</TableCell>
                <TableCell align="right">{statute.articleCount}</TableCell>
                <TableCell align="right">
                  <Button component={RouterLink} to={`/legal/${statute.id}`} size="small">
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
          aria-label="Paginación de leyes"
        />
      </Box>
    </Box>
  );
}

export function StatutePage() {
  const { statuteId = '' } = useParams();
  const { api } = useServices();
  const { has } = useCapabilities();
  const [statute, setStatute] = useState<StatuteDetail | null>(null);
  const [headings, setHeadings] = useState<HeadingView[]>([]);
  const [error, setError] = useState<UiError | null>(null);
  const [editing, setEditing] = useState<StatuteArticleView | 'new' | null>(null);

  const load = useCallback(async () => {
    try {
      const [detail, headingList] = await Promise.all([api.getStatute(statuteId), api.listHeadings()]);
      setStatute(detail);
      setHeadings(headingList);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, statuteId]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error && !statute) {
    return <Alert severity="error">{error.detail}</Alert>;
  }

  if (!statute) {
    return <Typography variant="body2">Cargando ley…</Typography>;
  }

  const canWrite = has('statute.write');
  const articles = [...statute.articles].sort((left, right) => left.number - right.number);
  const nextNumber = articles.length === 0 ? 1 : Math.max(...articles.map((a) => a.number)) + 1;

  return (
    <Box>
      <PageHeader
        title={statute.name}
        subtitle={`${statute.kind === 'LabourLaw' ? 'Ley laboral' : 'Otra norma'} · publicada ${statute.publishedOn ?? 'sin fecha'}`}
        actions={
          <Stack direction="row" spacing={1}>
            <Button component={RouterLink} to="/legal" size="small">
              Volver
            </Button>
            {canWrite ? (
              <Button variant="contained" size="small" onClick={() => setEditing('new')}>
                Añadir artículo
              </Button>
            ) : null}
          </Stack>
        }
      />

      {error ? <Alert severity="error" sx={{ mb: 2 }}>{error.detail}</Alert> : null}

      {articles.length === 0 ? (
        <EmptyState message="Esta ley todavía no tiene artículos." />
      ) : (
        <List aria-label="Artículos" dense>
          {articles.map((article) => (
            <ListItem
              key={article.id}
              alignItems="flex-start"
              secondaryAction={
                canWrite ? (
                  <Button size="small" onClick={() => setEditing(article)} aria-label={`Editar artículo ${article.number}`}>
                    Editar
                  </Button>
                ) : null
              }
            >
              <Box sx={{ pr: 10 }}>
                <Typography variant="subtitle1" component="p" sx={{ fontWeight: 600 }}>
                  Artículo {article.number}. {article.title}
                </Typography>
                <Typography variant="body2" component="div">
                  {article.body}
                </Typography>
              </Box>
            </ListItem>
          ))}
        </List>
      )}

      <ArticleDialog
        key={editing === null ? 'closed' : editing === 'new' ? 'new' : editing.id}
        open={editing !== null}
        article={editing === 'new' ? null : editing}
        headings={headings}
        defaultNumber={nextNumber}
        onClose={() => setEditing(null)}
        onSubmit={async (input) => {
          if (editing === 'new') {
            await api.createStatuteArticle(statute.id, input);
          } else if (editing) {
            await api.updateStatuteArticle(statute.id, editing.id, input, editing.rowVersion);
          }

          setEditing(null);
          await load();
        }}
      />
    </Box>
  );
}

function ArticleDialog({
  open,
  article,
  headings,
  defaultNumber,
  onClose,
  onSubmit,
}: {
  open: boolean;
  article: StatuteArticleView | null;
  headings: HeadingView[];
  defaultNumber: number;
  onClose: () => void;
  onSubmit: (input: StatuteArticleInput) => Promise<void>;
}) {
  const [number, setNumber] = useState(String(article?.number ?? defaultNumber));
  const [title, setTitle] = useState(article?.title ?? '');
  const [summary, setSummary] = useState(article?.summary ?? '');
  const [body, setBody] = useState(article?.body ?? '');
  const [headingId, setHeadingId] = useState(article?.headingId ?? '');
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md" aria-labelledby="article-dialog">
      <DialogTitle id="article-dialog">{article ? `Artículo ${article.number}` : 'Nuevo artículo'}</DialogTitle>
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
              label="Materia (opcional)"
              value={headingId}
              onChange={(event) => setHeadingId(event.target.value)}
              sx={{ flex: 1 }}
              helperText="Sin materia, el artículo no aparece en la comparación."
            >
              <MenuItem value="">Sin clasificar</MenuItem>
              {headings.map((heading) => (
                <MenuItem key={heading.id} value={heading.id}>
                  {heading.groupName} · {heading.name}
                  {heading.isExcluded ? ' (no se compara)' : ''}
                </MenuItem>
              ))}
            </TextField>
          </Stack>
          <TextField label="Título" value={title} onChange={(event) => setTitle(event.target.value)} required />
          <TextField label="Resumen" value={summary} onChange={(event) => setSummary(event.target.value)} />
          <TextField
            label="Texto"
            value={body}
            onChange={(event) => setBody(event.target.value)}
            required
            multiline
            minRows={6}
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
              requiredText(title, 'Indique el título del artículo.'),
              requiredText(body, 'Indique el texto del artículo.'),
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSubmit({
              number: Number(number),
              title: title.trim(),
              summary: summary.trim() === '' ? null : summary.trim(),
              body,
              headingId: headingId === '' ? null : headingId,
              economicComparisonNote: null,
            });
          }}
        >
          Guardar
        </Button>
      </DialogActions>
    </Dialog>
  );
}
