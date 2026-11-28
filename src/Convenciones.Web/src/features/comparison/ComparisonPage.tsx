import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import Collapse from '@mui/material/Collapse';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';
import Pagination from '@mui/material/Pagination';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useId, useState } from 'react';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { SECTIONS } from '../../lib/navigation';
import { formatValidity } from '../../lib/format';
import { useServices } from '../../services';
import type {
  ComparisonCandidate,
  ComparisonClauseView,
  ComparisonView,
  HeadingView,
} from '../../lib/api';
import { ApiError, describeError, type UiError } from '../../lib/errors';

const PAGE_SIZE = 5;

/**
 * The comparison workspace, as Q-6 defines it: clauses of one heading, adjacent.
 *
 * The screen never claims that two clauses are equivalent or aligned — the backend does not assert
 * that, and 899 clauses are unclassified, so the wording says what the data supports (T14 AC 2).
 */
export function ComparisonPage() {
  const { api } = useServices();
  const [headings, setHeadings] = useState<HeadingView[]>([]);
  const [headingId, setHeadingId] = useState('');
  const [candidates, setCandidates] = useState<ComparisonCandidate[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [result, setResult] = useState<ComparisonView | null>(null);
  const [page, setPage] = useState(1);
  const [error, setError] = useState<UiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);

  /**
   * Only comparable headings are offered here: an excluded heading ("No Compara") is a valid
   * classification for a clause, but the comparison never renders it.
   */
  const loadHeadings = useCallback(async (): Promise<HeadingView[]> => {
    const list = (await api.listHeadings()).filter((candidate) => !candidate.isExcluded);
    setHeadings(list);
    return list;
  }, [api]);

  useEffect(() => {
    void (async () => {
      try {
        const list = await loadHeadings();
        setHeadingId(list[0]?.id ?? '');
        if (list.length === 0) {
          setNotice('No hay ninguna materia comparable en la taxonomía; revise la biblioteca legal.');
        }
      } catch (failure) {
        setError(describeError(failure));
      } finally {
        setLoaded(true);
      }
    })();
  }, [loadHeadings]);

  useEffect(() => {
    if (headingId === '') {
      setCandidates([]);
      return;
    }

    void (async () => {
      try {
        const response = await api.listCandidates({ headingId, page: 1, pageSize: 50 });
        setCandidates(response.items);
        setError(null);
      } catch (failure) {
        // A heading that disappeared between loads (a taxonomy change, a stale selection) is not a
        // failure of the request the operator made: recover to the first usable heading and say so,
        // instead of leaving the engine's message on a blocked screen.
        const stale = failure instanceof ApiError && (failure.status === 400 || failure.status === 404);

        if (!stale) {
          setError(describeError(failure));
          return;
        }

        try {
          const list = await loadHeadings();
          const next = list[0]?.id ?? '';

          if (next !== '' && next !== headingId) {
            setNotice('La materia seleccionada ya no está disponible; se ha elegido la primera.');
            setHeadingId(next);
            return;
          }

          setCandidates([]);
          setError(describeError(failure));
        } catch (reloadFailure) {
          setError(describeError(reloadFailure));
        }
      }
    })();
  }, [api, headingId, loadHeadings]);

  const compare = useCallback(
    async (nextPage: number) => {
      if (headingId === '' || selected.length === 0) {
        setError({ code: 'validation_failed', detail: 'Seleccione una materia y al menos una convención publicada.' });
        return;
      }

      setLoading(true);
      setError(null);

      try {
        const view = await api.compare({
          headingId,
          agreementIds: selected,
          page: nextPage,
          pageSize: PAGE_SIZE,
        });
        setResult(view);
        setPage(nextPage);
      } catch (failure) {
        setError(describeError(failure));
      } finally {
        setLoading(false);
      }
    },
    [api, headingId, selected],
  );

  const heading = headings.find((candidate) => candidate.id === headingId);
  const headingDefinition = heading?.groupDescription ?? null;

  return (
    <Box>
      <PageHeader
        title={SECTIONS.comparison.label}
        subtitle={SECTIONS.comparison.help}
      />

      {error ? (
        <Alert severity="error" sx={{ mb: 2 }}>
          {error.detail}
        </Alert>
      ) : null}

      {notice ? (
        <Alert severity="info" sx={{ mb: 2 }} onClose={() => setNotice(null)}>
          {notice}
        </Alert>
      ) : null}

      {loaded && headings.length === 0 && !error ? (
        <EmptyState message="No hay materias comparables en la taxonomía todavía; un administrador tiene que revisarla." />
      ) : null}

      <Paper sx={{ p: 2, mb: 3 }}>
        <Stack spacing={2}>
          <TextField
            select
            label="Materia"
            value={headingId}
            onChange={(event) => {
              setHeadingId(event.target.value);
              setNotice(null);
              setResult(null);
              setSelected([]);
            }}
            required
            fullWidth
          >
            {headings.map((item) => (
              <MenuItem key={item.id} value={item.id}>
                {item.groupName} · {item.name}
              </MenuItem>
            ))}
          </TextField>

          {headingDefinition ? (
            <Box sx={{ borderLeft: 4, borderColor: 'primary.main', pl: 1.5 }}>
              <Typography variant="subtitle2" component="p">
                {heading?.groupName}
              </Typography>
              <Typography variant="body2" color="text.secondary" component="p" data-testid="heading-definition">
                {headingDefinition}
              </Typography>
            </Box>
          ) : null}

          <Box>
            <Typography variant="subtitle2" component="p" sx={{ mb: 1 }}>
              Convenciones publicadas ({candidates.length})
            </Typography>
            {candidates.length === 0 ? (
              <Typography variant="body2" color="text.secondary">
                No hay convenciones publicadas para comparar todavía.
              </Typography>
            ) : (
              <Stack direction="row" flexWrap="wrap" gap={1}>
                {candidates.map((candidate) => (
                  <FormControlLabel
                    key={candidate.agreementId}
                    control={
                      <Checkbox
                        checked={selected.includes(candidate.agreementId)}
                        onChange={(event) =>
                          setSelected((current) =>
                            event.target.checked
                              ? [...current, candidate.agreementId]
                              : current.filter((id) => id !== candidate.agreementId),
                          )
                        }
                      />
                    }
                    label={`${candidate.title} (${candidate.clauseCount} cláusulas)`}
                  />
                ))}
              </Stack>
            )}
          </Box>

          <Button
            variant="contained"
            onClick={() => void compare(1)}
            disabled={loading || selected.length === 0 || headingId === ''}
          >
            Comparar
          </Button>
        </Stack>
      </Paper>

      {result ? (
        <Box>
          <Typography variant="h6" component="h2" sx={{ mb: 0.5 }}>
            {result.heading.groupName} · {result.heading.name}
          </Typography>
          <Typography variant="caption" color="text.secondary" component="p" sx={{ mb: 2 }}>
            Vecindad por materia, sin alineación: las cláusulas se muestran unas junto a otras porque tratan la
            misma materia, no porque sean equivalentes.
            {result.heading.requiresEconomicComparison
              ? ' Esta materia requiere comparación económica.'
              : ''}
          </Typography>

          {headingDefinition ? (
            <Typography variant="body2" color="text.secondary" component="p" sx={{ mb: 2 }}>
              <strong>{result.heading.groupName}:</strong> {headingDefinition}
            </Typography>
          ) : null}

          {result.unpublishedRequested.length > 0 ? (
            <Alert severity="warning" sx={{ mb: 2 }}>
              Algunas convenciones solicitadas no están publicadas y no se muestran: {result.unpublishedRequested.join(', ')}.
            </Alert>
          ) : null}

          <Paper sx={{ p: 2, mb: 2 }} aria-label="Biblioteca legal de esta materia">
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
              Biblioteca legal de esta materia
            </Typography>
            <Typography variant="caption" color="text.secondary" component="p" sx={{ mb: 1 }}>
              Artículos de la biblioteca legal clasificados en «{result.heading.groupName}». Corresponden a la
              materia, no a cada cláusula: no hay emparejamiento uno a uno entre artículo y cláusula.
            </Typography>
            {result.statuteArticles.length === 0 ? (
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                Ningún artículo de la biblioteca legal está clasificado en esta materia.
              </Typography>
            ) : (
              <Stack spacing={1.5} sx={{ mt: 1 }}>
                {result.statuteArticles.map((article) => (
                  <Box key={article.articleId}>
                    <Typography variant="subtitle2" component="p">
                      {article.statuteName} · art. {article.number}
                    </Typography>
                    <Typography variant="body2">{article.title}</Typography>
                    <Typography variant="caption" color="text.secondary" component="div">
                      {article.body}
                    </Typography>
                  </Box>
                ))}
              </Stack>
            )}
            {result.statuteArticleTotal > result.statuteArticles.length ? (
              <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 1 }}>
                Se muestran {result.statuteArticles.length} de {result.statuteArticleTotal} artículos.
              </Typography>
            ) : null}
          </Paper>

          <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr', md: 'repeat(2, 1fr)' } }}>
            {result.agreements.map((agreement) => (
              <Paper key={agreement.agreementId} sx={{ p: 2 }} aria-label={`Cláusulas de ${agreement.title}`}>
                <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                  {agreement.title}
                </Typography>
                <Typography variant="caption" color="text.secondary" component="p">
                  {agreement.companyName} · vigencia {formatValidity(agreement.validFrom, agreement.validTo)}
                </Typography>
                {agreement.clauses.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                    Sin cláusulas en esta materia.
                  </Typography>
                ) : (
                  <Stack spacing={1.5} sx={{ mt: 1 }}>
                    {agreement.clauses.map((clause) => (
                      <ComparedClause key={clause.clauseId} clause={clause} />
                    ))}
                  </Stack>
                )}
              </Paper>
            ))}
          </Box>

          <Box sx={{ display: 'flex', justifyContent: 'center', mt: 3 }}>
            <Pagination
              count={Math.max(1, Math.ceil(result.total / PAGE_SIZE))}
              page={page}
              onChange={(_, value) => void compare(value)}
              aria-label="Paginación de la comparación"
            />
          </Box>
        </Box>
      ) : heading ? (
        <EmptyState message="Seleccione convenciones publicadas y pulse Comparar." />
      ) : null}
    </Box>
  );
}

/**
 * One compared clause: the summary first, the full text on demand (#138a).
 *
 * The base text is the summary when it says something and the body otherwise, so a clause without a
 * summary is never rendered empty. The toggle appears only when the body carries text the summary does
 * not already show. `Collapse` unmounts the body while it is closed, so the same words are not in the
 * document twice.
 *
 * A clause is never called "artículo": that word belongs to the legal library, and the screen keeps the
 * adjacency wording (no clause is declared equivalent to another, ADR-028).
 */
function ComparedClause({ clause }: { clause: ComparisonClauseView }) {
  const [expanded, setExpanded] = useState(false);
  const contentId = useId();
  const summary = clause.summary?.trim() ? clause.summary : clause.body;
  const hasMore = clause.body.trim() !== '' && clause.body.trim() !== summary.trim();

  return (
    <Box>
      <Typography variant="subtitle2" component="p">
        {clause.number}. {clause.title}
        {clause.isExcludedFromCompilation ? ' (excluida)' : ''}
      </Typography>

      <Box id={contentId}>
        {expanded ? null : (
          <Typography variant="body2" component="div" data-testid="clause-summary">
            {summary}
          </Typography>
        )}
        <Collapse in={expanded} unmountOnExit>
          <Typography variant="body2" component="div" data-testid="clause-body">
            {clause.body}
          </Typography>
        </Collapse>
      </Box>

      {clause.economicComparisonNote ? (
        <Typography variant="caption" color="text.secondary" component="div">
          Nota económica: {clause.economicComparisonNote}
        </Typography>
      ) : null}

      {hasMore ? (
        <Button
          size="small"
          aria-expanded={expanded}
          aria-controls={contentId}
          onClick={() => setExpanded((current) => !current)}
          sx={{ mt: 0.5 }}
        >
          {expanded ? 'Ocultar cláusula completa' : 'Ver cláusula completa'}
        </Button>
      ) : null}
    </Box>
  );
}
