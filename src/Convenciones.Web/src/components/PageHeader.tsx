import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

/** The heading every screen shares: a title, an optional subtitle and the screen's actions. */
export function PageHeader({
  title,
  subtitle,
  actions,
}: {
  title: string;
  subtitle?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <Box
      component="header"
      sx={{
        display: 'flex',
        flexWrap: 'wrap',
        gap: 2,
        alignItems: 'flex-start',
        justifyContent: 'space-between',
        mb: 3,
      }}
    >
      <Box sx={{ minWidth: 0 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 700 }}>
          {title}
        </Typography>
        {subtitle ? (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            {subtitle}
          </Typography>
        ) : null}
      </Box>
      {actions ? <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap' }}>{actions}</Box> : null}
    </Box>
  );
}

/** A short, consistent empty state. It says what is missing, never that data is wrong. */
export function EmptyState({ message }: { message: string }) {
  return (
    <Box sx={{ p: 4, textAlign: 'center' }}>
      <Typography variant="body2" color="text.secondary">
        {message}
      </Typography>
    </Box>
  );
}
