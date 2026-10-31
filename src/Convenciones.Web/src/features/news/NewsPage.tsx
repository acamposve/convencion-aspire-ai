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
import Stack from '@mui/material/Stack';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import TextField from '@mui/material/TextField';
import { useCallback, useEffect, useState } from 'react';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { useCapabilities } from '../auth/useCapabilities';
import { useServices } from '../../services';
import type { NewsCategoryView, NewsSummary, PublicationState } from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { formatDateTime } from '../../lib/format';
import { firstError, requiredText } from '../../lib/forms';

const PAGE_SIZE = 10;

export function NewsPage() {
  const { api } = useServices();
  const { has } = useCapabilities();
  const [items, setItems] = useState<NewsSummary[]>([]);
  const [categories, setCategories] = useState<NewsCategoryView[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [state, setState] = useState<PublicationState | ''>('');
  const [error, setError] = useState<UiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    try {
      const [list, categoryList] = await Promise.all([
        api.listNews({ page, pageSize: PAGE_SIZE, state: state === '' ? null : state }),
        api.listNewsCategories(),
      ]);
      setItems(list.items);
      setTotal(list.total);
      setCategories(categoryList);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, [api, page, state]);

  useEffect(() => {
    void load();
  }, [load]);

  const change = async (id: string, action: 'publish' | 'unpublish') => {
    try {
      if (action === 'publish') {
        await api.publishNews(id);
        setNotice('Noticia publicada.');
      } else {
        await api.unpublishNews(id);
        setNotice('Noticia despublicada.');
      }

      await load();
    } catch (failure) {
      setError(describeError(failure));
    }
  };

  return (
    <Box>
      <PageHeader
        title="Noticias"
        subtitle="Contenido del portal público. Publicar requiere la capacidad news.publish; la fecha y el autor los registra el servidor."
        actions={
          has('news.write') ? (
            <Button variant="contained" onClick={() => setCreating(true)}>
              Nueva noticia
            </Button>
          ) : null
        }
      />

      {notice ? <Alert severity="success" sx={{ mb: 2 }} onClose={() => setNotice(null)}>{notice}</Alert> : null}
      {error ? <Alert severity="error" sx={{ mb: 2 }}>{error.detail}</Alert> : null}

      <TextField
        select
        size="small"
        label="Estado"
        value={state}
        onChange={(event) => {
          setPage(1);
          setState(event.target.value as PublicationState | '');
        }}
        sx={{ minWidth: 180, mb: 2 }}
      >
        <MenuItem value="">Todos</MenuItem>
        <MenuItem value="Draft">Borradores</MenuItem>
        <MenuItem value="Published">Publicadas</MenuItem>
      </TextField>

      {items.length === 0 ? (
        <EmptyState message="No hay noticias." />
      ) : (
        <Table size="small" aria-label="Noticias">
          <TableHead>
            <TableRow>
              <TableCell>Título</TableCell>
              <TableCell>Categoría</TableCell>
              <TableCell>Estado</TableCell>
              <TableCell>Publicada</TableCell>
              <TableCell />
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((article) => (
              <TableRow key={article.id}>
                <TableCell>{article.title}</TableCell>
                <TableCell>{article.categoryName}</TableCell>
                <TableCell>
                  <Chip size="small" label={article.state === 'Published' ? 'Publicada' : 'Borrador'} />
                </TableCell>
                <TableCell>{article.publishedAt ? formatDateTime(article.publishedAt) : '—'}</TableCell>
                <TableCell align="right">
                  {has('news.publish') ? (
                    <Button
                      size="small"
                      onClick={() => void change(article.id, article.state === 'Published' ? 'unpublish' : 'publish')}
                    >
                      {article.state === 'Published' ? 'Despublicar' : 'Publicar'}
                    </Button>
                  ) : null}
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
          aria-label="Paginación de noticias"
        />
      </Box>

      <CreateNewsDialog
        open={creating}
        categories={categories}
        onClose={() => setCreating(false)}
        onSubmit={async (input) => {
          await api.createNews(input);
          setCreating(false);
          setNotice('Noticia creada como borrador.');
          await load();
        }}
      />
    </Box>
  );
}

function CreateNewsDialog({
  open,
  categories,
  onClose,
  onSubmit,
}: {
  open: boolean;
  categories: NewsCategoryView[];
  onClose: () => void;
  onSubmit: (input: {
    categoryId: string;
    title: string;
    summary: string | null;
    body: string;
  }) => Promise<void>;
}) {
  const [categoryId, setCategoryId] = useState(categories[0]?.id ?? '');
  const [title, setTitle] = useState('');
  const [summary, setSummary] = useState('');
  const [body, setBody] = useState('');
  const [error, setError] = useState<string | null>(null);

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md" aria-labelledby="news-dialog">
      <DialogTitle id="news-dialog">Nueva noticia</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          {categories.length === 0 ? (
            <Alert severity="info">
              No hay categorías de noticias. Un administrador debe crear una antes de publicar.
            </Alert>
          ) : null}
          <TextField
            select
            label="Categoría"
            value={categoryId}
            onChange={(event) => setCategoryId(event.target.value)}
            required
          >
            {categories.map((category) => (
              <MenuItem key={category.id} value={category.id}>
                {category.name}
              </MenuItem>
            ))}
          </TextField>
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
              categoryId === '' ? 'Seleccione una categoría.' : null,
              requiredText(title, 'Indique el título.'),
              requiredText(body, 'Indique el texto.'),
            ]);

            if (validation) {
              setError(validation);
              return;
            }

            void onSubmit({
              categoryId,
              title: title.trim(),
              summary: summary.trim() === '' ? null : summary.trim(),
              body,
            }).catch((failure: unknown) => setError(describeError(failure).detail));
          }}
        >
          Crear
        </Button>
      </DialogActions>
    </Dialog>
  );
}
