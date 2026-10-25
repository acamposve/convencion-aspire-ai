/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the API, for example http://localhost:5000. Empty means same origin. */
  readonly VITE_API_BASE_URL?: string;
  /** Supabase project URL. Public by design. */
  readonly VITE_SUPABASE_URL?: string;
  /** Supabase publishable (anon) key. Public by design; RLS is the control. */
  readonly VITE_SUPABASE_ANON_KEY?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
