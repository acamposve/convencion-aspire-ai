import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';
import Pagination from '@mui/material/Pagination';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useState } from 'react';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { SECTIONS } from '../../lib/navigation';
import { useServices } from '../../services';
import type { ComparisonCandidate, ComparisonView, HeadingView } from '../../lib/api';
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

          {result.unpublishedRequested.length > 0 ? (
            <Alert severity="warning" sx={{ mb: 2 }}>
              Algunas convenciones solicitadas no están publicadas y no se muestran: {result.unpublishedRequested.join(', ')}.
            </Alert>
          ) : null}

          <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr', md: 'repeat(2, 1fr)' } }}>
            <Paper sx={{ p: 2 }} aria-label="Artículos de la ley">
              <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                Ley ({result.statuteArticleTotal})
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
            </Paper>

            {result.agreements.map((agreement) => (
              <Paper key={agreement.agreementId} sx={{ p: 2 }} aria-label={`Cláusulas de ${agreement.title}`}>
                <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                  {agreement.title}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  {agreement.companyName} · vigencia {agreement.validFrom} – {agreement.validTo ?? 'sin fin'}
                </Typography>
                {agreement.clauses.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                    Sin cláusulas en esta materia.
                  </Typography>
                ) : (
                  <Stack spacing={1.5} sx={{ mt: 1 }}>
                    {agreement.clauses.map((clause) => (
                      <Box key={clause.clauseId}>
                        <Typography variant="subtitle2" component="p">
                          {clause.number}. {clause.title}
                          {clause.isExcludedFromCompilation ? ' (excluida)' : ''}
                        </Typography>
                        <Typography variant="body2" component="div">
                          {clause.body}
                        </Typography>
                        {clause.economicComparisonNote ? (
                          <Typography variant="caption" color="text.secondary" component="div">
                            Nota económica: {clause.economicComparisonNote}
                          </Typography>
                        ) : null}
                      </Box>
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
