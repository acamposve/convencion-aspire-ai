import { createContext, useContext, type ReactNode } from 'react';
import type { ApiClient, PublicApi } from './lib/api';
import type { AuthClient } from './lib/auth';

export interface AppServices {
  auth: AuthClient;
  api: ApiClient;
  /** The anonymous client the public portal uses. It holds no token. */
  publicApi: PublicApi;
}

const ServicesContext = createContext<AppServices | null>(null);

export function ServicesProvider({
  services,
  children,
}: {
  services: AppServices;
  children: ReactNode;
}) {
  return <ServicesContext.Provider value={services}>{children}</ServicesContext.Provider>;
}

export function useServices(): AppServices {
  const services = useContext(ServicesContext);

  if (services === null) {
    throw new Error('useServices must be used inside ServicesProvider.');
  }

  return services;
}
