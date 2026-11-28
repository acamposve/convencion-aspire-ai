/**
 * The one place where the section names live (#139).
 *
 * The client calls the signed agreement a **convención** and the negotiation process a **bitácora**.
 * Those words were spread across the menu, the page titles and the help text, so renaming them meant
 * hunting strings in five files. They are collected here instead: changing a name is one edit, and the
 * menu and the page headers cannot drift apart.
 *
 * Routes are deliberately untouched. `/agreements` and `/negotiation` stay, because renaming a label is
 * not renaming a resource, and the API contract, the capability keys and the database keep their names.
 *
 * The words also matter inside each screen: "acuerdo" is reserved for the **acuerdo de reunión** of the
 * bitácora, so a convention is never called an agreement again.
 */

export interface NavItem {
  /** Router path. Never derived from the label. */
  to: string;
  label: string;
  /** The capability that makes the destination useful; hiding it is a courtesy, the API is the guard. */
  capability: string;
  /** One sentence shown under the section title, in the client's words. */
  help?: string;
}

export interface NavGroup {
  key: string;
  /** Visible grouping name; null for destinations that stand on their own. */
  label: string | null;
  items: NavItem[];
}

export const SECTIONS = {
  documents: {
    to: '/documents',
    label: 'Documentos',
    capability: 'document.read',
  },
  agreements: {
    to: '/agreements',
    label: 'Convenciones',
    capability: 'agreement.read',
    help: 'Convenciones colectivas por empresa y vigencia, con sus cláusulas. El título se compone de la empresa y los años; no se almacena.',
  },
  comparison: {
    to: '/comparison',
    label: 'Comparación',
    capability: 'comparison.read',
    help: 'Cláusulas de convenciones publicadas, agrupadas por materia. La pantalla no afirma equivalencia: muestra vecindad.',
  },
  negotiation: {
    to: '/negotiation',
    label: 'Bitácora',
    capability: 'negotiation.read',
    help: 'Las reuniones de negociación por caso: peticiones, ofertas y acuerdos de reunión. Las etapas son explícitas: iniciar, pendiente, acordar, cerrar y reabrir.',
  },
  legal: {
    to: '/legal',
    label: 'Biblioteca legal',
    capability: 'statute.read',
  },
  news: {
    to: '/news',
    label: 'Noticias',
    capability: 'news.read',
  },
  users: {
    to: '/admin/users',
    label: 'Usuarios',
    capability: 'user.manage',
  },
  roles: {
    to: '/admin/roles',
    label: 'Roles',
    capability: 'role.manage',
  },
  audit: {
    to: '/audit',
    label: 'Auditoría',
    capability: 'audit.read',
  },
} as const satisfies Record<string, NavItem>;

/**
 * The menu, in the order the work happens: the PDF comes in, the convention is authored, compared and
 * negotiated, and the legal library backs all of it.
 *
 * "Convenciones colectivas" is the one visible group: it is the name the client uses for these three
 * screens in the system he knows, and grouping them answers the question that opened #139.
 */
export const NAV_GROUPS: NavGroup[] = [
  { key: 'documents', label: null, items: [SECTIONS.documents] },
  {
    key: 'agreements',
    label: 'Convenciones colectivas',
    items: [SECTIONS.agreements, SECTIONS.comparison, SECTIONS.negotiation],
  },
  { key: 'reference', label: null, items: [SECTIONS.legal, SECTIONS.news] },
  { key: 'administration', label: null, items: [SECTIONS.users, SECTIONS.roles, SECTIONS.audit] },
];

/** Flat list, for lookups by path. */
export const NAV_ITEMS: readonly NavItem[] = NAV_GROUPS.flatMap((group) => group.items);
