import { createTheme } from '@mui/material/styles';

/**
 * The MUI theme is the entire styling system. There is no Tailwind and no CSS framework.
 *
 * The palette is deliberately restrained: this is a legal-document workspace, and status colour is
 * reserved for status. The layout relies on MUI breakpoints, so the same components work at 360 px.
 */
export const theme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#173f5f' },
    secondary: { main: '#0f766e' },
    background: { default: '#f5f7f9', paper: '#ffffff' },
    error: { main: '#b3261e' },
    warning: { main: '#9a6700' },
    success: { main: '#1b6e3c' },
    info: { main: '#0b5cad' },
  },
  typography: {
    fontFamily: [
      'system-ui',
      '-apple-system',
      'Segoe UI',
      'Roboto',
      'Helvetica',
      'Arial',
      'sans-serif',
    ].join(','),
    h4: { fontWeight: 700, letterSpacing: '-0.01em' },
    h5: { fontWeight: 700 },
    button: { textTransform: 'none', fontWeight: 600 },
  },
  shape: { borderRadius: 10 },
  components: {
    MuiButton: {
      defaultProps: { disableElevation: true },
    },
    MuiPaper: {
      defaultProps: { elevation: 0 },
    },
    MuiTextField: {
      defaultProps: { fullWidth: true },
    },
  },
});
