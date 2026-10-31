import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Divider from '@mui/material/Divider';
import Pagination from '@mui/material/Pagination';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Tab from '@mui/material/Tab';
import Tabs from '@mui/material/Tabs';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useState } from 'react';
import { PageHeader, EmptyState } from '../../components/PageHeader';
import { useServices } from '../../services';
import type {
  PublicAgreementDetail,
  PublicAgreementSummary,
  PublicNewsDetail,
  PublicNewsSummary,
} from '../../lib/api';
import { describeError, type UiError } from '../../lib/errors';
import { formatDateTime } from '../../lib/format';

const PAGE_SIZE = 10;

/**
 * The anonymous portal.
 *
 * It reads through `publicApi`, which has no token and sends no Authorization header. The server is the
 * real guard: `/public/*` runs on the `anon` database role and returns 404 for anything unpublished
 * (T12). Rendering here must therefore work with no session at all (T14 AC 4).
 */
export function PortalPage() {
  const { publicApi } = useServices();
  const [tab, setTab] = useState<'agreements' | 'news'>('agreements');
  const [agreements, setAgreements] = useState<PublicAgreementSummary[]>([]);
  const [agreementTotal, setAgreementTotal] = useState(0);
  const [news, setNews] = useState<PublicNewsSummary[]>([]);
  const [newsTotal, setNewsTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [agreementDetail, setAgreementDetail] = useState<PublicAgreementDetail | null>(null);
  const [newsDetail, setNewsDetail] = useState<PublicNewsDetail | null>(null);
  const [error, setError] = useState<UiError | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);

    try {
      if (tab === 'agreements') {
        const response = await publicApi.listAgreements(page, PAGE_SIZE);
        setAgreements(response.items);
        setAgreementTotal(response.total);
      } else {
        const response = await publicApi.listNews(page, PAGE_SIZE);
        setNews(response.items);
        setNewsTotal(response.total);
      }
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setLoading(false);
    }
  }, [page, publicApi, tab]);

  useEffect(() => {
    void load();
  }, [load]);

  const openAgreement = async (id: string) => {
    setError(null);

    try {
      setAgreementDetail(await publicApi.getAgreement(id));
    } catch (failure) {
      setError(describeError(failure));
    }
  };

  const openNews = async (id: string) => {
    setError(null);

    try {
      setNewsDetail(await publicApi.getNews(id));
    } catch (failure) {
      setError(describeError(failure));
    }
  };

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default', py: 4 }}>
      <Container maxWidth="md">
        <Typography variant="overline" color="text.secondary">
          Portal público
        </Typography>
        <PageHeader
          title="Convenciones colectivas publicadas"
          subtitle="Contenido público: acuerdos publicados y noticias. No requiere sesión y no muestra datos internos."
        />

        {error ? (
          <Alert severity="error" sx={{ mb: 2 }}>
            {error.detail}
          </Alert>
        ) : null}

        <Tabs
          value={tab}
          onChange={(_, value: 'agreements' | 'news') => {
            setTab(value);
            setPage(1);
            setAgreementDetail(null);
            setNewsDetail(null);
          }}
          aria-label="Contenido público"
          sx={{ mb: 2 }}
        >
          <Tab value="agreements" label="Acuerdos" />
          <Tab value="news" label="Noticias" />
        </Tabs>

        {tab === 'agreements' && agreementDetail ? (
          <Paper sx={{ p: 3 }}>
            <Button size="small" onClick={() => setAgreementDetail(null)} sx={{ mb: 1 }}>
              ← Volver
            </Button>
            <Typography variant="h5" component="h2" sx={{ fontWeight: 700 }}>
              {agreementDetail.title}
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              {agreementDetail.companyName} · vigencia {agreementDetail.validFrom} –{' '}
              {agreementDetail.validTo ?? 'sin fin'} · publicado {formatDateTime(agreementDetail.publishedAt)}
            </Typography>

            <Stack spacing={2}>
              {agreementDetail.clauses.map((clause) => (
                <Box key={`${clause.number}-${clause.title}`}>
                  <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 600 }}>
                    {clause.number}. {clause.title}
                  </Typography>
                  <Typography variant="body2" component="div" dangerouslySetInnerHTML={{ __html: clause.body }} />
                </Box>
              ))}
            </Stack>

            {agreementDetail.annexes.length > 0 ? (
              <>
                <Divider sx={{ my: 3 }} />
                <Typography variant="h6" component="h3">
                  Anexos
                </Typography>
                {agreementDetail.annexes.map((annex) => (
                  <Box key={annex.name} sx={{ mt: 1 }}>
                    <Typography variant="subtitle2" component="p">
                      {annex.name}
                    </Typography>
                    {annex.description ? <Typography variant="body2">{annex.description}</Typography> : null}
                    {annex.body ? (
                      <Typography variant="body2" component="div" dangerouslySetInnerHTML={{ __html: annex.body }} />
                    ) : null}
                  </Box>
                ))}
              </>
            ) : null}
          </Paper>
        ) : tab === 'news' && newsDetail ? (
          <Paper sx={{ p: 3 }}>
            <Button size="small" onClick={() => setNewsDetail(null)} sx={{ mb: 1 }}>
              ← Volver
            </Button>
            <Typography variant="overline" color="text.secondary">
              {newsDetail.categoryName}
            </Typography>
            <Typography variant="h5" component="h2" sx={{ fontWeight: 700 }}>
              {newsDetail.title}
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              {formatDateTime(newsDetail.publishedAt)}
            </Typography>
            {newsDetail.summary ? (
              <Typography variant="subtitle1" sx={{ mb: 2 }}>
                {newsDetail.summary}
              </Typography>
            ) : null}
            <Typography variant="body1" component="div" dangerouslySetInnerHTML={{ __html: newsDetail.body }} />
          </Paper>
        ) : tab === 'agreements' ? (
          <Stack spacing={1.5}>
            {loading ? (
              <Typography variant="body2">Cargando acuerdos publicados…</Typography>
            ) : agreements.length === 0 ? (
              <EmptyState message="Todavía no hay acuerdos publicados." />
            ) : (
              agreements.map((agreement) => (
                <Paper key={agreement.id} sx={{ p: 2 }}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2, flexWrap: 'wrap' }}>
                    <Box>
                      <Typography variant="subtitle1" component="h2" sx={{ fontWeight: 600 }}>
                        {agreement.title}
                      </Typography>
                      <Typography variant="body2" color="text.secondary">
                        {agreement.companyName} · vigencia {agreement.validFrom} – {agreement.validTo ?? 'sin fin'} ·
                        ámbito {agreement.scope}
                      </Typography>
                    </Box>
                    <Button size="small" onClick={() => void openAgreement(agreement.id)}>
                      Leer
                    </Button>
                  </Box>
                </Paper>
              ))
            )}
            <Pagination
              count={Math.max(1, Math.ceil(agreementTotal / PAGE_SIZE))}
              page={page}
              onChange={(_, value) => setPage(value)}
              sx={{ alignSelf: 'center', mt: 2 }}
              aria-label="Paginación de acuerdos publicados"
            />
          </Stack>
        ) : (
          <Stack spacing={1.5}>
            {loading ? (
              <Typography variant="body2">Cargando noticias…</Typography>
            ) : news.length === 0 ? (
              <EmptyState message="Todavía no hay noticias publicadas." />
            ) : (
              news.map((article) => (
                <Paper key={article.id} sx={{ p: 2 }}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2, flexWrap: 'wrap' }}>
                    <Box>
                      <Typography variant="overline" color="text.secondary">
                        {article.categoryName} · {formatDateTime(article.publishedAt)}
                      </Typography>
                      <Typography variant="subtitle1" component="h2" sx={{ fontWeight: 600 }}>
                        {article.title}
                      </Typography>
                      {article.summary ? (
                        <Typography variant="body2" color="text.secondary">
                          {article.summary}
                        </Typography>
                      ) : null}
                    </Box>
                    <Button size="small" onClick={() => void openNews(article.id)}>
                      Leer
                    </Button>
                  </Box>
                </Paper>
              ))
            )}
            <Pagination
              count={Math.max(1, Math.ceil(newsTotal / PAGE_SIZE))}
              page={page}
              onChange={(_, value) => setPage(value)}
              sx={{ alignSelf: 'center', mt: 2 }}
              aria-label="Paginación de noticias publicadas"
            />
          </Stack>
        )}
      </Container>
    </Box>
  );
}
